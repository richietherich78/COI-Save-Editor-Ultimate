using System.IO;
using System.Reflection;
using COISaveEditorUltimate.Models;
using COISaveEditorUltimate.Parsing;

namespace COISaveEditorUltimate.DeepEdit;

public sealed partial class DeepEditEngine
{
    // ── Reflected types (fetched once after assemblies are loaded) ────────

    private Type? _tBlobReader;
    private Type? _tBlobWriter;
    private Type? _tMemoryBlobWriter;
    private Type? _tDependencyResolver;
    private Type? _tOption;
    private Type? _tImmutableArray;

    private ConstructorInfo? _miBlobReaderCtor;
    private MethodInfo? _miDeserializeInto;
    private MethodInfo? _miCreateEmpty;
    private MethodInfo? _miSerialize;
    private MethodInfo? _miFinalizeLoading;
    private MethodInfo? _miReadULong;
    private MethodInfo? _miSetSpecialSerializers;   // BlobReader.SetSpecialSerializers
    private FieldInfo?  _fiReadObjects;
    private FieldInfo?  _fiReadTypes;

    // Captured data from the read phase — used by ReserialiseAllChunks
    private object? _capturedSaveInfo;        // GameSaveInfo object
    private Array?  _capturedConfigsArray;    // IConfig[] (underlying array from ImmutableArray<IConfig>)
    private object? _gameSpecialSerializers;  // ImmutableArray<ISpecialSerializerFactory> — cached for reader+writer
    private object? _populatedProtosDb;       // ProtosDb instance — injected into resolver for Phase 2
    private HashSet<object>? _phantomProtoStubs; // Proto stubs created for mod protos not in our ProtosDb
    // Entities removed by StripBrokenTrajectoryEntities (in addition to the mod-assembly
    // entities removed earlier). Used by the IoPort scrub to also sever ports owned by
    // these entities so the renderer doesn't NRE on orphaned ports.
    private HashSet<object>? _strippedBrokenEntities;
    private HashSet<Type>? _lystStructDiagnosticDumped; // diagnostic: track first encounter per Lyst<TStruct> elem type
    private readonly HashSet<string> _phase3SafeMethods = new(StringComparer.Ordinal);

    // ── Public API ────────────────────────────────────────────────────────

    public sealed class DeepEditResult
    {
        public bool   Success    { get; init; }
        public string? Error     { get; init; }
        public int     ObjectsRemoved { get; init; }
        public List<string> RemovedTypeNames { get; init; } = new();
        public byte[]? OutputBytes { get; init; }
        /// <summary>When set, the output was written directly to this file path
        /// instead of being held in <see cref="OutputBytes"/>.</summary>
        public string? OutputFilePath { get; init; }
        /// <summary>Full detailed log for diagnostics (written to .log file).</summary>
        public string DetailedLog { get; init; } = string.Empty;
        /// <summary>Validator report for the produced payload. Populated even on success.
        /// When <see cref="SaveOutputValidator.Report.IsClean"/> is false and
        /// <see cref="DeepEditOptions.AllowBrokenSaveOutput"/> was false, deep edit fails
        /// and no .save file is produced.</summary>
        public SaveOutputValidator.Report? ValidatorReport { get; init; }
    }

    /// <summary>Optional behaviour controls for <see cref="Execute"/>. All defaults are safe.</summary>
    public sealed class DeepEditOptions
    {
        /// <summary>
        /// When false (default), if the validator detects type-name strings in the produced
        /// payload that reference removed-mod assemblies, deep edit refuses to write the .save
        /// and reports the violations. Set this to true ONLY for debugging — the produced
        /// save is guaranteed to crash the game on load.
        /// </summary>
        public bool AllowBrokenSaveOutput { get; init; }
    }

    /// <summary>
    /// Attempts to fully deserialise the RESOLVER, strip objects belonging
    /// to <paramref name="modsToRemove"/>, re-serialise, and return the new
    /// save file bytes.
    ///
    /// Must be called AFTER <see cref="AssemblyLoader.Load"/> has completed.
    /// </summary>
    public DeepEditResult Execute(ParsedSave save, ISet<string> modsToRemove,
                                   IProgress<string>? progress = null,
                                   string? outputFilePath = null,
                                   DeepEditOptions? options = null)
    {
        options ??= new DeepEditOptions();
        var detailLog = new System.Text.StringBuilder();
        void Log(string msg)
        {
            detailLog.AppendLine(msg);
            progress?.Report(msg);
        }

        try
        {
            progress?.Report("[STEP:1:8:Initializing…]");
            Log("Resolving reflected types…");
            if (!BindReflectedTypes(out string? bindErr))
                return FailWithLog(bindErr!, detailLog);

            // ── Build the decompressed stream ─────────────────────────────
            byte[] decompressed = save.DecompressedData;
            using var ms = new MemoryStream(decompressed, writable: false);
            ms.Position = 0;

            // ── Create BlobReader ─────────────────────────────────────────
            Log("Creating BlobReader…");
            object reader = CreateBlobReader(ms, save.SaveVersion);
            IProgress<string> logProgress = new SyncProgress(Log);
            TrySetSpecialSerializersForConfigs(reader, logProgress);

            // ── Read MOD_TYPES + SAVE_INFO + CONFIGS chunks ───────────────
            progress?.Report("[STEP:2:8:Reading headers…]");
            Log("Reading header chunks…");
            ReadHeaderChunks(reader, logProgress);

            // ── Set game-level special serializers before RESOLVER ─────────
            TrySetGameSpecialSerializers(reader, logProgress);

            // ── Create an empty DependencyResolver ───────────────────────
            Log("Building populated DependencyResolver from loaded assemblies…");

            // Build assembly name set for removed mods up-front so we can filter them
            // out of the populated-resolver build (their managers must NOT be auto-
            // instantiated during Phase 2/3) and reuse it for phantom stripping later.
            var stripAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in modsToRemove)
            {
                stripAssemblies.Add(id.Replace('-', '.'));
                stripAssemblies.Add(id);
            }
            var removedAsmNamesEarly = BuildRemovedAssemblyNames(modsToRemove, stripAssemblies);

            // Mirrors GameBuilder.RegisterModDependenciesOrThrow so [GlobalDependency]
            // classes — most importantly EntityContext — are auto-instantiated when
            // Phase 2 resolves them. Without this, every Storage/Transport/Machine
            // updateProperties() NREs because base.Context is null.
            object? populated = BuildPopulatedResolver(removedAsmNamesEarly, logProgress);
            object resolver;
            if (populated is not null)
            {
                resolver = populated;
            }
            else
            {
                Log("Falling back to empty DependencyResolver (Phase 2 EntityContext lookups will likely fail).");
                resolver = _miCreateEmpty!.Invoke(null, null)!;
            }

            // ── DeserializeInto ────────────────────────────────────────────
            progress?.Report("[STEP:3:8:Deserializing RESOLVER…]");
            Log("Deserialising RESOLVER chunk (this may take a moment)…");
            ReadResolverChunkHeader(reader);
            _miDeserializeInto!.Invoke(null, new[] { resolver, reader });

            // ── Inject ProtosDb into resolver for Phase 2 member resolution ──
            // Always inject after DeserializeInto: deserializeData() calls AddAndAssertNew
            // for every saved object, which in release builds silently overwrites the
            // entry pre-registered by BuildPopulatedResolver. After that overwrite
            // TryResolve(typeof(ProtosDb)) can still return None for complex reasons
            // (registration vs. real-type dict divergence). Forcing the injection here
            // via the Dict indexer setter guarantees the entry is present before Phase 2
            // resolves UnlockedProtosDb.m_protosDb, preventing a null-ref during RESOLVER
            // re-serialization that would otherwise produce an ~89 KB output file.
            if (_populatedProtosDb is not null)
            {
                InjectProtosDbIntoResolver(resolver, _populatedProtosDb, logProgress);
            }

            // ── FinalizeLoading ────────────────────────────────────────────
            progress?.Report("[STEP:4:8:Finalizing load (Phase 1–3)…]");
            Log("Finalising load (resolving members + draining delayed reads)…");
            // Always use best-effort Phase 1+2 with Phase 3 skipped. Direct path
            // (FinalizeLoadingTimeSliced) runs the game's own Phase 3 which, with a
            // populated resolver doing real manager registrations, takes ~15s per
            // entity (136K entities ≈ many hours). Phase 3 is safe to skip here
            // because the game re-runs it from a clean state when loading the save.
            var failedPhase1Objects = InvokeFinalizeLoading(
                reader, resolver, logProgress, detailLog,
                preferDirect: false);

            // ── Identify + remove objects from unwanted mods ──────────────
            progress?.Report("[STEP:5:8:Filtering mod objects…]");
            Log("Filtering objects from removed mods…");
            var (removedCount, removedTypes) = StripRemovedModObjects(resolver, modsToRemove, logProgress);

            // Note: stripAssemblies + removedAsmNamesEarly were built earlier (before resolver
            // construction) so the populated-resolver builder could exclude them.

            // ── Only strip Phase 1 failures that belong to REMOVED mods ─────
            if (failedPhase1Objects.Count > 0)
            {

                var modFailedObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (var obj in failedPhase1Objects)
                {
                    string asmName = obj.GetType().Assembly.GetName().Name ?? "";
                    if (stripAssemblies.Contains(asmName))
                        modFailedObjects.Add(obj);
                }

                Log($"Phase 1 had {failedPhase1Objects.Count} failure(s) total, {modFailedObjects.Count} from removed mods.");
                if (modFailedObjects.Count > 0)
                {
                    Log($"Stripping {modFailedObjects.Count} mod object(s) whose data deserialization failed…");
                    var (failedRemoved, failedTypes) = StripSpecificObjects(resolver, modFailedObjects, logProgress);
                    removedCount += failedRemoved;
                    removedTypes.AddRange(failedTypes);
                }
            }

            // ── Build proto healing lookup before stripping so entity/GoalsList phantom
            //    protos can be replaced with vanilla equivalents rather than stripped ──
            progress?.Report("[STEP:5a:8:Building proto healing lookup…]");
            Log("Building proto healing lookup for phantom proto recovery…");
            _protoHealingLookup = BuildProtoHealingLookup(logProgress);

            // ── Strip entities whose prototype resolved to null ──────────
            StripNullProtoEntities(resolver, stripAssemblies, logProgress);
            StripNullProtoEntitiesFromManagers(resolver, stripAssemblies, logProgress);
            // Strip any entity whose concrete runtime type is from a removed-mod assembly,
            // even if its proto was healed to vanilla (e.g. SmartZipper healed to Zipper proto
            // but the SmartZipper class itself requires the mod to deserialize).
            StripRemovedModTypeEntitiesFromManagers(resolver, stripAssemblies, logProgress);

            // Strip vanilla entities (Transport, Lift, Conveyor, …) whose internal trajectory
            // state is unusable because the mod-side initialiser that would have set up
            // m_pivots is gone. Without this, Transport.initAfterLoad NREs inside
            // TransportTrajectory.tryCreateCurveFromPivots, and OceanEntitiesManager.
            // rebuildFromExistingEntities subsequently NREs on the same broken Transports
            // — fatal to game-load.
            StripBrokenTrajectoryEntities(resolver, stripAssemblies, logProgress);

            // ── Audit: report every phantom proto reference before we touch anything ──
            // This gives a complete picture of what needs cleaning. Output goes to the log.
            progress?.Report("[STEP:5b:8:Auditing phantom proto references…]");
            AuditPhantomProtoRefs(resolver, logProgress);

            // ── Remove phantom stubs from all resolver collections ─────────
            // Must run before NullifyPhantomProtoIds so stubs are still identifiable by reference.
            // Handles cases like ResearchManager unlocked-node lists where phantom stubs
            // are stored directly rather than by ID.
            progress?.Report("[STEP:5c:8:Stripping phantom proto collection refs…]");
            StripPhantomProtoRefsFromCollections(resolver, stripAssemblies, logProgress);

            // Targeted top-level scrub for Option<T> fields whose T comes from a removed
            // mod. Catches managers (e.g. AssetTransactionManager.m_globalBuffers holding
            // a Lyst<GlobalBuffer> with Option<COIExtended…FishFarm> per item) that the
            // BFS above didn't reach because they live behind a Lyst-of-struct field.
            progress?.Report("[STEP:5d:8:Scrubbing top-level Option<T> for removed-mod T…]");
            ScrubTopLevelRemovedModOptionFields(resolver, stripAssemblies, logProgress);

            // Final catch-all: walk the whole reachable object graph (resolver + entities)
            // and null every reference field that holds a phantom proto stub. This is the
            // safety net behind every structural scrub above; without it, ~15 stubs survive
            // in containing types we don't have a dedicated branch for (ProductProto,
            // ActiveLoan, settlement modules, …) and the validator catches them on output.
            progress?.Report("[STEP:5e:8:Catch-all phantom-ref null pass…]");
            NullAllPhantomStubReferences(resolver, logProgress);

            // Null any reference from vanilla objects to already-stripped mod entities
            // (e.g. IoPort.m_connected pointing at a SmartZipper/FishFarm that was stripped).
            // Without this pass, BlobWriter follows those dangling refs during serialisation
            // and writes the mod entity's AQN → CorruptedSaveException on game load.
            progress?.Report("[STEP:5f:8:Null dangling removed-mod entity refs…]");
            NullAllRemovedModEntityReferences(resolver, stripAssemblies, logProgress);

            // Drop saved IMessageNotification entries that reference null/phantom protos.
            // The notification UI (ResearchTab, MessagesTab) dereferences proto fields
            // directly during construction; any null/phantom there → NullReferenceException
            // during InstantiateAllAndLock → fatal game-load failure with
            // DependencyResolverException ("Failed to instantiate ResearchTab").
            // Notifications carry zero gameplay impact (UI history only), so we can
            // safely drop the broken ones without affecting save integrity.
            progress?.Report("[STEP:5g:8:Drop broken saved notifications…]");
            DropBrokenSavedMessageNotifications(resolver, stripAssemblies, logProgress);

            // Targeted pass: iterate IoPortsManager.m_ports directly to sever
            // vanilla IoPort → stripped IoPort connections that the BFS budget
            // can't reliably reach (SmartZipper/FishFarm/CargoShipDrydock AQN path).
            progress?.Report("[STEP:5g:8:Scrub dangling IoPort connections…]");
            ScrubDanglingIoPortConnections(resolver, stripAssemblies, logProgress);

            // ── Final safety sweep: purge any remaining mod-assembly objects from
            // m_resolvedObjects that individual passes may have missed (e.g. entities
            // that were registered in the resolver but not removed by StripSpecificObjects
            // because the Remove reflection call silently failed).
            PurgeModAssemblyObjectsFromResolver(resolver, stripAssemblies, logProgress);

            // ── Fix up objects whose Phase 3 didn't fully run ─────────────
            PrepareObjectsForReserialization(resolver, logProgress);

            // ── Nullify phantom proto IDs so the game doesn't encounter unresolvable IDs
            NullifyPhantomProtoIds(logProgress);

            // ── Drop EventBase<T> callback entries whose Owner is a removed-mod entity.
            // EventBase.m_callbacksSaveData is a Lyst<CallbackSaveData> where each struct
            // slot's Owner field holds the captured target of an Action subscriber. The
            // BlobWriter serialises Owner via WriteGeneric, which writes its full AQN +
            // fields inline — so a SmartZipper or CargoShipDrydock captured as a callback
            // target survives every reachability-based scrub. This pass walks every
            // Lyst<CallbackSaveData> in the resolver graph and compacts mod-typed-Owner
            // slots out of the backing array.
            DropEventBaseCallbackEntriesForStrippedOwners(resolver, stripAssemblies, logProgress);

            // BlobWriter serialises Type fields and Lyst<Type> elements via WriteType,
            // which writes the full AQN of any Type whose Assembly isn't mscorlib or the
            // executing assembly. So ANY stripped-mod Type stored as a value (Type field
            // OR Lyst<Type> element OR Dict<Type,*> key) survives every reachability scrub
            // and produces a validator violation. The game's own loader confirmed this:
            // failed deserialization of 'Lyst`1[System.Type].ToString() threw NRE' immediately
            // after an Event<Percent>, with the failing AQN being a stripped-mod entity type.
            // Walk the entire deserialised graph and null-out / drop every stripped-mod Type.
            DropStrippedModTypeReferences(resolver, stripAssemblies, logProgress);

            // ── Last-line-of-defence diagnostic: dump every mod-assembly object that
            // is STILL inside any resolver collection at this exact moment, immediately
            // before serialisation. The BlobWriter is about to write whatever it sees,
            // so anything reported here is what the validator will catch on output.
            // Pure logging — does not modify anything; safe for already-passing saves.
            DumpAnyRemainingModAssemblyObjectsInResolver(resolver, stripAssemblies, logProgress);

            // ── Re-serialise ALL chunks through one BlobWriter ────────────
            progress?.Report("[STEP:6:8:Re-serializing…]");
            Log("Re-serialising all chunks (MOD_TYPES → SAVE_INFO → CONFIGS → RESOLVER)…");
            byte[] decompressedPayload = ReserialiseAllChunks(
                save, modsToRemove, _capturedSaveInfo, _capturedConfigsArray,
                resolver, logProgress);

            // ── Validate output for Mono incompatible type references ────────
            progress?.Report("[STEP:6b:8:Validating output…]");
            ValidateNoPrivateCoreLib(decompressedPayload, logProgress);

            // ── Validate output for unloadable (removed-mod) type references ──
            // This is the *authoritative* gate. If any type-name string in the produced
            // payload references an assembly the game cannot load, the save will crash
            // on load with CorruptedSaveException ("Failed to load type from '…'").
            // Surface the offending type strings now and refuse to write the .save
            // unless explicitly overridden via DeepEditOptions.AllowBrokenSaveOutput.
            Log("Validating output against loadable-type index…");
            // Expand validator search list to all sub-assemblies of removed mod families.
            // modsToRemove may contain IDs like "COIExtended-Core" whose family root is
            // "COIExtended". We scan all loaded assemblies for any whose simple name equals
            // the root or starts with "root." / "root-", so that COIExtended.Automation
            // (not explicitly listed) is still caught as a removed-mod assembly.
            var removedAsmNames = BuildRemovedAssemblyNames(modsToRemove, stripAssemblies);
            Log($"  Removed-assembly set ({removedAsmNames.Count}): {string.Join(", ", removedAsmNames.OrderBy(x => x))}");
            var loadableIdx = LoadableTypeIndex.Build(removedAsmNames.ToArray());
            Log($"  Loadable type index built: {loadableIdx.Count} type names from " +
                $"{loadableIdx.LoadableAssemblySimpleNames.Count} kept assembly(ies).");

            // ── Last-line-of-defence: in-place AQN patcher ─────────────────────────
            // Before we hand the bytes to the validator, scan for any AQN that
            // references a removed-mod assembly and rewrite it in place to a benign,
            // same-byte-length AQN (System.Object in mscorlib). The reflection-based
            // scrubs cannot reach every BlobWriter call site that emits a Type AQN
            // (CallbackSaveData.DeclaringType inside an EventBase Lyst whose holder
            // is invisible to a graph walk is the canonical case). This patch keeps
            // every byte offset and length-prefix in the stream unchanged, so the
            // game's BlobReader sees a structurally identical payload with a
            // resolvable type token in place of the removed-mod AQN.
            int aqnPatched = StrippedTypeAqnPatcher.Patch(decompressedPayload, removedAsmNames, logProgress);
            if (aqnPatched > 0)
                Log($"  [AqnPatch] Rewrote {aqnPatched} stripped-mod AQN occurrence(s) in produced payload.");

            var validatorReport = SaveOutputValidator.Validate(decompressedPayload, removedAsmNames);
            if (validatorReport.IsClean)
            {
                Log("  [VALIDATOR] OK — no removed-mod type references in produced payload.");
            }
            else
            {
                Log($"  [VALIDATOR] {validatorReport.Violations.Count} unique type string(s) " +
                    $"({validatorReport.TotalOccurrences} occurrence(s)) reference removed-mod assemblies:");
                int shown = 0;
                foreach (var v in validatorReport.Violations.OrderByDescending(x => x.OccurrenceCount))
                {
                    Log($"    × {v.OccurrenceCount,5}  {Truncate(v.TypeStringSnippet, 220)}  (first @ 0x{v.FirstOffset:X})");
                    if (!string.IsNullOrEmpty(v.NearestPrecedingType))
                        Log($"            nearest preceding type: {v.NearestPrecedingType}");
                    if (!string.IsNullOrEmpty(v.PrecedingBytesDump))
                    {
                        Log("            preceding type-name strings (parent→child chain — bottom is most recent before hit):");
                        Log(v.PrecedingBytesDump);
                    }
                    if (++shown >= 25) { Log($"    … and {validatorReport.Violations.Count - shown} more (suppressed)."); break; }
                }
                if (!options.AllowBrokenSaveOutput)
                {
                    Log("  [VALIDATOR] Refusing to write .save (AllowBrokenSaveOutput=false). " +
                        "Each line above is a future game-load crash. Add a scrub pass for these and re-run, " +
                        "or set AllowBrokenSaveOutput=true to ship anyway (will crash the game on load).");
                    return new DeepEditResult
                    {
                        Success          = false,
                        Error            = $"Validator: {validatorReport.Violations.Count} unloadable type string(s) " +
                                           $"({validatorReport.TotalOccurrences} occurrence(s)) survive in the produced payload. " +
                                           "See log for the full list.",
                        ObjectsRemoved   = removedCount,
                        RemovedTypeNames = removedTypes,
                        DetailedLog      = detailLog.ToString(),
                        ValidatorReport  = validatorReport,
                    };
                }
                Log("  [VALIDATOR] AllowBrokenSaveOutput=true — proceeding to write a save the game cannot load.");
            }

            // ── Release the massive deserialized object graph ──────────────
            ClearCapturedState();
            resolver = null!;
            failedPhase1Objects.Clear();
            Log("Released deserialized object graph — forcing GC…");
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            long memAfterGc = GC.GetTotalMemory(false);
            Log($"Post-GC memory: {memAfterGc / (1024.0 * 1024.0):F0} MB");

            // ── Build the final .save file ─────────────────────────────────
            progress?.Report("[STEP:7:8:Compressing…]");
            Log("Compressing and building save file…");

            if (outputFilePath is not null)
            {
                BuildSaveFileToFile(save, decompressedPayload, outputFilePath);
                decompressedPayload = null!;

                progress?.Report("[STEP:8:8:Done]");
                Log($"Done — {removedCount} object(s) removed.");
                return new DeepEditResult
                {
                    Success          = true,
                    ObjectsRemoved   = removedCount,
                    RemovedTypeNames = removedTypes,
                    OutputFilePath   = outputFilePath,
                    DetailedLog      = detailLog.ToString(),
                    ValidatorReport  = validatorReport,
                };
            }

            byte[] outputBytes = BuildSaveFile(save, decompressedPayload);
            decompressedPayload = null!;

            progress?.Report("[STEP:8:8:Done]");
            Log($"Done — {removedCount} object(s) removed.");
            return new DeepEditResult
            {
                Success          = true,
                ObjectsRemoved   = removedCount,
                RemovedTypeNames = removedTypes,
                OutputBytes      = outputBytes,
                DetailedLog      = detailLog.ToString(),
                ValidatorReport  = validatorReport,
            };
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            var inner = tie.InnerException;
            var msg = $"{inner.GetType().Name}: {inner.Message}\n\nStack trace:\n{inner.StackTrace}";
            Log($"FATAL: {msg}");
            return FailWithLog(msg, detailLog);
        }
        catch (Exception ex)
        {
            var msg = $"{ex.GetType().Name}: {ex.Message}\n\nStack trace:\n{ex.StackTrace}";
            Log($"FATAL: {msg}");
            return FailWithLog(msg, detailLog);
        }
        finally
        {
            ClearCapturedState();
        }
    }

    /// <summary>
    /// Deserializes and re-serializes the save without removing any objects.
    /// Use this to verify that the serialization layer is correct independently
    /// of the stripping logic.  If the output fails to load, the bug is in
    /// serialization/compatibility — not in object stripping.
    /// </summary>
    public DeepEditResult ExecuteRoundTrip(ParsedSave save,
                                           IProgress<string>? progress = null,
                                           string? outputFilePath = null)
        => Execute(save, new HashSet<string>(), progress, outputFilePath);

    /// <summary>Truncates a string for safe single-line log display.</summary>
    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

    /// <summary>
    /// Builds the comprehensive set of assembly simple-names that should be treated
    /// as "removed" for validator and loadable-type-index purposes.
    ///
    /// Starts from <paramref name="stripAssemblies"/> (exact IDs already derived from
    /// <paramref name="modsToRemove"/>) then scans every loaded assembly for any whose
    /// name belongs to the same mod family as a removed mod.
    ///
    /// Family root extraction: "COIExtended-Core" → dot-normalised "COIExtended.Core"
    /// → root = "COIExtended" (everything before the first '.').  A loaded assembly is
    /// in the family if its name equals the root, or starts with "root." or "root-".
    /// This ensures e.g. COIExtended.Automation is found even when only
    /// COIExtended-Core appears in <paramref name="modsToRemove"/>.
    /// </summary>
    internal static HashSet<string> BuildRemovedAssemblyNames(
        ISet<string>? modsToRemove,
        HashSet<string> stripAssemblies)
    {
        var result = new HashSet<string>(stripAssemblies, StringComparer.OrdinalIgnoreCase);
        if (modsToRemove is null || modsToRemove.Count == 0) return result;

        // Derive family roots: "COIExtended-Core" → dotId "COIExtended.Core" → root "COIExtended"
        var familyRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in modsToRemove)
        {
            string dotId = id.Replace('-', '.');
            int firstDot = dotId.IndexOf('.');
            string root = firstDot > 0 ? dotId.Substring(0, firstDot) : dotId;
            familyRoots.Add(root);
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = asm.GetName().Name ?? "";
            if (string.IsNullOrEmpty(name)) continue;
            foreach (var root in familyRoots)
            {
                if (name.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(root + ".", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(root + "-", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(name);
                    break;
                }
            }
        }
        return result;
    }

    // ── Memory management ────────────────────────────────────────────────

    /// <summary>
    /// Releases all large captured state from the deserialization phase.
    /// Called after re-serialisation completes (or on failure) so the GC can
    /// reclaim the enormous object graph (resolver, protos, configs, etc.).
    /// </summary>
    private void ClearCapturedState()
    {
        _capturedSaveInfo      = null;
        _capturedConfigsArray  = null;
        _gameSpecialSerializers = null;
        _populatedProtosDb     = null;
        _phantomProtoStubs?.Clear();
        _phantomProtoStubs     = null;
        _strippedBrokenEntities?.Clear();
        _strippedBrokenEntities = null;
        _lystStructDiagnosticDumped = null;
        _protoHealingLookup    = null;
        _phase3SafeMethods.Clear();
    }

    // ── Config special serializers ────────────────────────────────────────

    /// <summary>
    /// Attempts (best-effort) to call SetSpecialSerializers on the BlobReader using
    /// SpecialSerializerFactories.GetSerializersForConfigs() so that ProtoId-typed
    /// config fields deserialize correctly.
    /// </summary>
    private void TrySetSpecialSerializersForConfigs(object reader, IProgress<string>? progress)
    {
        if (_miSetSpecialSerializers is null) return;
        try
        {
            var tFactory = AssemblyLoader.FindType("Mafi.Core.Game.SpecialSerializerFactories");
            if (tFactory is null) { progress?.Report("  Note: SpecialSerializerFactories not found — configs without ProtoId data will still work."); return; }

            var miGet = tFactory.GetMethod("GetSerializersForConfigs",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (miGet is null) { progress?.Report("  Note: GetSerializersForConfigs not found (private method search failed)."); return; }

            var serializers = miGet.Invoke(null, null);
            if (serializers is null) return;
            _miSetSpecialSerializers.Invoke(reader, new[] { serializers });
            progress?.Report("  Config special serializers applied.");
        }
        catch (Exception ex)
        {
            progress?.Report($"  Note: Could not set config special serializers: {ex.Message}");
        }
    }

    // ── Game special serializer switch ──────────────────────────────────

    /// <summary>
    /// Sets game-level special serializers on the BlobReader before
    /// deserialising the RESOLVER chunk.  The critical serializer is
    /// ProtosSerializerFactory which handles Proto types (like TerrainMaterialProto).
    /// Without it, ReadGenericAs&lt;Proto&gt; fails with "Failed to create generic
    /// deserializer", which corrupts the BlobReader stream position and
    /// cascades 174+ failures through Phase 1.
    /// </summary>
    private void TrySetGameSpecialSerializers(object reader, IProgress<string>? progress)
    {
        if (_miSetSpecialSerializers is null) return;
        try
        {
            _gameSpecialSerializers ??= BuildGameSpecialSerializersArray(progress);
            if (_gameSpecialSerializers is not null)
            {
                _miSetSpecialSerializers.Invoke(reader, new[] { _gameSpecialSerializers });
                progress?.Report("  Game special serializers applied to reader.");
            }
            else
            {
                progress?.Report("  WARNING: Could not create game special serializers — Phase 1 will likely fail.");
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"  WARNING: Failed to apply game special serializers: {ex.Message}");
        }
    }

    /// <summary>Synchronous IProgress — calls the action on the current thread.</summary>
    private sealed class SyncProgress(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }
}
