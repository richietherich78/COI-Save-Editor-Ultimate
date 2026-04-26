using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace COISaveEditorUltimate.DeepEdit;

/// <summary>
/// Direct invocation of the game's <c>BlobReader.FinalizeLoadingTimeSliced</c>.
/// This is the preferred Phase 1/2/3 path when a populated resolver is available,
/// because it is guaranteed to match the exact order, priority sorting, and member
/// resolution semantics the game itself uses (see
/// <c>workspace-files\api-decompiled-0.8.2\Mafi.Export\Mafi.Serialization\BlobReader.cs</c>,
/// method <c>FinalizeLoadingTimeSliced</c>).
/// </summary>
public sealed partial class DeepEditEngine
{
    /// <summary>
    /// Tries to drive <c>BlobReader.FinalizeLoadingTimeSliced(Some(resolver), 100, null)</c>
    /// to completion. Returns <c>true</c> on success (Phase 1/2/3 ran without throwing),
    /// <c>false</c> if any required reflection target is missing or if the iterator
    /// throws — in which case the caller should fall back to <c>InvokeFinalizeLoadingBestEffort</c>.
    /// </summary>
    /// <remarks>
    /// We do NOT wrap the iterator with per-method timeouts here. The game's iterator
    /// runs Phase 3 init calls in priority order with a real, populated resolver, so
    /// individual entity init failures (NREs because their state is partially missing
    /// after mod removal) cause the iterator to throw — and the whole pass aborts.
    /// That's why we keep the best-effort wrapper as a fallback: it logs each failure,
    /// auto-skips after a per-method threshold, and returns the failed object set so
    /// downstream code can strip them.
    /// <para/>
    /// FUTURE: if direct-path success rate is low for real saves, we can interleave
    /// per-iterator-step exception handling around <c>etor.MoveNext</c> to recover from
    /// individual entity failures without losing the priority ordering benefit.
    /// </remarks>
    private bool TryFinalizeLoadingDirect(
        object reader, object resolver, System.IProgress<string>? progress)
    {
        try
        {
            var tBlobReader = reader.GetType();
            var tOption = AssemblyLoader.FindType("Mafi.Option`1");
            var tDR = AssemblyLoader.FindType("Mafi.DependencyResolver");
            if (tOption is null || tDR is null)
            {
                progress?.Report("  [direct-finalize] Mafi.Option`1 or Mafi.DependencyResolver not found.");
                return false;
            }

            // Construct Option<DependencyResolver>.Some(resolver) via reflection.
            var optionOfDR = tOption.MakeGenericType(tDR);
            var miSome = optionOfDR.GetMethod(
                "Some",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { tDR },
                modifiers: null);
            if (miSome is null)
            {
                progress?.Report("  [direct-finalize] Option<DependencyResolver>.Some(T) not found.");
                return false;
            }
            object resolverOption = miSome.Invoke(null, new[] { resolver })!;

            // Find FinalizeLoadingTimeSliced(Option<DR>, int, Action) — the game's
            // public iterator. Match by name + 3-arg signature whose first param's
            // generic-type-definition is Option<>.
            MethodInfo? miFinalize = null;
            foreach (var mi in tBlobReader.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (mi.Name != "FinalizeLoadingTimeSliced") continue;
                var ps = mi.GetParameters();
                if (ps.Length != 3) continue;
                if (!ps[0].ParameterType.IsGenericType) continue;
                if (ps[0].ParameterType.GetGenericTypeDefinition() != tOption) continue;
                if (ps[1].ParameterType != typeof(int)) continue;
                if (ps[2].ParameterType != typeof(Action)) continue;
                miFinalize = mi;
                break;
            }
            if (miFinalize is null)
            {
                progress?.Report("  [direct-finalize] BlobReader.FinalizeLoadingTimeSliced(Option<DR>, int, Action) not found.");
                return false;
            }

            // Drive the iterator. We pass a large time budget so the game batches as
            // much Phase-3 init work as possible per MoveNext() call. The original
            // 100ms budget caused 1 init call per slice when each initSelf does real
            // manager-registration work (136K × 100ms ≈ 3.8 hours). At 30s per slice
            // the iterator runs until it either finishes or a single entity consumes
            // 30s — comparable to the game's own non-time-sliced load path.
            const int PauseAfterMs = 30_000;
            var enumerator = miFinalize.Invoke(reader, new object?[] { resolverOption, PauseAfterMs, null }) as System.Collections.IEnumerator;
            if (enumerator is null)
            {
                progress?.Report("  [direct-finalize] FinalizeLoadingTimeSliced returned null enumerator.");
                return false;
            }

            int slices = 0;
            var sw = Stopwatch.StartNew();
            string lastMessage = "";
            while (enumerator.MoveNext())
            {
                slices++;
                lastMessage = enumerator.Current as string ?? lastMessage;
                progress?.Report($"  [direct-finalize] slice {slices}: {lastMessage}");
                sw.Restart();
            }
            progress?.Report($"  [direct-finalize] Completed in {slices} slice(s). Last status: {lastMessage}");
            return true;
        }
        catch (Exception ex)
        {
            var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
            progress?.Report($"  [direct-finalize] FinalizeLoadingTimeSliced threw: {inner.GetType().Name}: {inner.Message}");
            return false;
        }
    }

    /// <summary>
    /// Tries direct-path FinalizeLoading first (matches the production game pipeline).
    /// On any failure, falls back to the best-effort hand-rolled phases. Returns the
    /// set of Phase-1-failed objects (empty if direct path succeeds, since direct path
    /// either runs cleanly or throws all-or-nothing).
    /// </summary>
    private HashSet<object> InvokeFinalizeLoading(
        object reader, object resolver,
        System.IProgress<string>? progress,
        System.Text.StringBuilder? detailLog = null,
        bool preferDirect = true)
    {
        if (preferDirect)
        {
            progress?.Report("Attempting direct FinalizeLoading (game pipeline)…");
            if (TryFinalizeLoadingDirect(reader, resolver, progress))
            {
                progress?.Report("Direct FinalizeLoading succeeded — skipping best-effort fallback.");
                return new HashSet<object>(ReferenceEqualityComparer.Instance);
            }
            progress?.Report("Direct FinalizeLoading failed — falling back to per-item best-effort phases (Phase 3 skipped: populated resolver).");
            // Phase 3 MUST be skipped when a populated resolver is in use: initSelf
            // callbacks register entities into live managers (PowerManager,
            // WaterPollutionManager, etc.) and that mutated state gets serialised,
            // causing CorruptedSaveException on game-side load (type mismatch at the
            // stream offset where Phase 3 inserted unexpected references).
            // The game runs its own Phase 3 cleanly on load, so we don't need ours.
            return InvokeFinalizeLoadingBestEffort(reader, resolver, progress, detailLog, skipPhase3: true);
        }
        // skipPhase3: true — Phase 3 must always be skipped when a populated resolver
        // is in use, regardless of which path we took. Running initSelf callbacks
        // mutates live manager state that then gets serialised, corrupting the save.
        return InvokeFinalizeLoadingBestEffort(reader, resolver, progress, detailLog, skipPhase3: true);
    }
}
