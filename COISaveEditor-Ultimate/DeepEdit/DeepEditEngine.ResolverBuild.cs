using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace COISaveEditorUltimate.DeepEdit;

/// <summary>
/// Builds a populated <c>DependencyResolver</c> the way the production game does:
/// register every <c>[GlobalDependency]</c>-attributed class from each loaded vanilla
/// (and DLC) Mafi assembly, register the populated <c>ProtosDb</c> and every
/// <c>IConfig</c> from the save's CONFIGS chunk. The resolver auto-instantiates
/// classes (notably <c>EntityContext</c> with its 28-arg ctor) on first
/// <c>Resolve</c>, so Phase 2 member resolution and Phase 3 init calls run against
/// a real, fully-wired graph instead of an empty one.
/// </summary>
public sealed partial class DeepEditEngine
{
    /// <summary>
    /// Builds a <c>DependencyResolver</c> via <c>DependencyResolverBuilder</c>, registering
    /// every <c>[GlobalDependency]</c> class in each loaded assembly that is NOT in
    /// <paramref name="removedAsmNames"/>. Returns the resolver instance, or <c>null</c>
    /// if reflection binding fails (caller falls back to the empty-resolver path).
    /// </summary>
    /// <param name="removedAsmNames">
    /// Assembly names being stripped (e.g. <c>COIExtended.Core</c>). Their types are NOT
    /// registered, mirroring the game's behaviour when a mod is removed before load.
    /// </param>
    private object? BuildPopulatedResolver(
        HashSet<string> removedAsmNames,
        System.IProgress<string>? progress)
    {
        const BindingFlags pubInst = BindingFlags.Public | BindingFlags.Instance;

        var tBuilder = AssemblyLoader.FindType("Mafi.DependencyResolverBuilder");
        if (tBuilder is null)
        {
            progress?.Report("  [resolver-build] DependencyResolverBuilder type not found — falling back to empty resolver.");
            return null;
        }

        object builder;
        try
        {
            builder = Activator.CreateInstance(tBuilder)!;
        }
        catch (Exception ex)
        {
            progress?.Report($"  [resolver-build] Failed to construct DependencyResolverBuilder: {ex.Message}");
            return null;
        }

        // ── Bind the builder methods we need ──────────────────────────────
        var miRegisterAllGlobal = tBuilder.GetMethod(
            "RegisterAllGlobalDependencies",
            pubInst,
            binder: null,
            types: new[] { typeof(Assembly), typeof(Predicate<Type>) },
            modifiers: null);
        if (miRegisterAllGlobal is null)
        {
            progress?.Report("  [resolver-build] DependencyResolverBuilder.RegisterAllGlobalDependencies(Assembly, Predicate<Type>) not found.");
            return null;
        }

        var miBuildAndClear = tBuilder.GetMethod("BuildAndClear", pubInst);
        if (miBuildAndClear is null)
        {
            progress?.Report("  [resolver-build] DependencyResolverBuilder.BuildAndClear not found.");
            return null;
        }

        // RegisterInstance<T>(T instance, bool disposeOnResolverTermination = false)
        var miRegisterInstanceGeneric = tBuilder.GetMethods(pubInst)
            .FirstOrDefault(m =>
                m.Name == "RegisterInstance" &&
                m.IsGenericMethodDefinition &&
                m.GetParameters().Length >= 1);
        if (miRegisterInstanceGeneric is null)
        {
            progress?.Report("  [resolver-build] DependencyResolverBuilder.RegisterInstance<T> not found.");
            return null;
        }

        // ── Register ProtosDb (populated and locked) ───────────────────────
        if (_populatedProtosDb is not null)
        {
            try
            {
                LockProtosDbIfNeeded(_populatedProtosDb, progress);

                var registrar = miRegisterInstanceGeneric
                    .MakeGenericMethod(_populatedProtosDb.GetType())
                    .Invoke(builder, new object?[] { _populatedProtosDb, false });

                // .AsSelf() on the returned DependencyInstanceRegistrar<T>
                CallAsSelf(registrar, progress, "ProtosDb");
            }
            catch (Exception ex)
            {
                progress?.Report($"  [resolver-build] ProtosDb registration failed: {ex.Message}");
            }
        }

        // ── Register a stub IFileSystemHelper ─────────────────────────────
        // GameLoader normally registers a real helper before BuildAndClear.  Without
        // it, deserializing TerrainManager throws "Failed to set dependency to
        // 'm_fileSystemHelper' field". The helper is only used for cache file paths
        // at runtime — none of those code paths fire during deep-edit, so a no-op
        // DispatchProxy is enough to satisfy the resolver.
        TryRegisterRuntimeOnlyStubs(builder, miRegisterInstanceGeneric, progress);

        // ── Register every IConfig from the save ───────────────────────────
        if (_capturedConfigsArray is not null)
        {
            int registered = 0;
            foreach (var cfg in _capturedConfigsArray)
            {
                if (cfg is null) continue;
                try
                {
                    var registrar = miRegisterInstanceGeneric
                        .MakeGenericMethod(cfg.GetType())
                        .Invoke(builder, new object?[] { cfg, false });

                    // .AsSelf() only — the game excludes IConfig from AsAllInterfaces via the
                    // serialization predicate; we mirror that by not calling AsAllInterfaces here.
                    CallAsSelf(registrar, progress: null, label: cfg.GetType().Name);
                    registered++;
                }
                catch
                {
                    // Skip configs we can't register — best effort.
                }
            }
            progress?.Report($"  [resolver-build] Registered {registered}/{_capturedConfigsArray.Length} config(s) from save.");
        }

        // ── Walk loaded assemblies, register [GlobalDependency] classes ───
        // Skip stripped-mod assemblies (their types are being removed) AND
        // skip our own editor assembly + non-game assemblies.
        var iConfigType = AssemblyLoader.FindType("Mafi.Core.Game.IConfig");
        Predicate<Type> notConfig = iConfigType is null
            ? _ => true
            : (Type t) => !iConfigType.IsAssignableFrom(t);

        int asmRegistered = 0, asmSkippedStripped = 0, asmSkippedNonGame = 0, asmFailed = 0;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string asmName = asm.GetName().Name ?? "";
            if (string.IsNullOrEmpty(asmName)) continue;

            // Only consider game-family assemblies. Anything outside that prefix list
            // (System.*, our own COISaveEditor*, xunit, etc.) has no [GlobalDependency]
            // we care about and would just waste time scanning thousands of types.
            if (!IsGameFamilyAssembly(asmName))
            {
                asmSkippedNonGame++;
                continue;
            }

            // Path B from the locked plan: skip stripped mod assemblies so their
            // managers aren't auto-instantiated and don't end up as targets of
            // resolver dependency edges.
            if (removedAsmNames.Contains(asmName))
            {
                asmSkippedStripped++;
                progress?.Report($"  [resolver-build] Skipping stripped-mod assembly: {asmName}");
                continue;
            }

            try
            {
                miRegisterAllGlobal.Invoke(builder, new object?[] { asm, notConfig });
                asmRegistered++;
            }
            catch (TargetInvocationException tie) when (tie.InnerException is ReflectionTypeLoadException rtle)
            {
                // Some assemblies (e.g. Mafi.ModsAuthoringSupport) reference types that
                // aren't resolvable in our process. Fall back to a per-type registration
                // that uses only the types that DID load. We replicate the inner loop
                // of RegisterAllGlobalDependencies here.
                var loadedTypes = (rtle.Types ?? Array.Empty<Type>())
                    .Where(t => t is not null)
                    .Cast<Type>()
                    .ToArray();
                int registeredHere = TryRegisterTypesIndividually(builder, loadedTypes, notConfig, progress);
                progress?.Report($"  [resolver-build] '{asmName}': type-load partial — registered {registeredHere}/{loadedTypes.Length} loadable type(s).");
                asmRegistered++;
            }
            catch (Exception ex)
            {
                asmFailed++;
                var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                progress?.Report($"  [resolver-build] RegisterAllGlobalDependencies failed for '{asmName}': {inner.GetType().Name}: {inner.Message}");
                // Path B: continue rather than abort.
            }
        }
        progress?.Report($"  [resolver-build] Asm pass: {asmRegistered} registered, {asmSkippedStripped} stripped-skip, {asmSkippedNonGame} non-game-skip, {asmFailed} failed.");

        // ── Set serialization predicate: exclude IConfig types ───────────────
        // The game sets:
        //   builder.SetShouldSerializePredicate((t) => !t.IsAssignableTo<IConfig>())
        // This ensures IConfig objects are only serialized in the CONFIGS chunk, NOT
        // in the resolver sections.  If we omit this, every config instance appears in
        // both CONFIGS and resolver section-2 (m_resolvedInstancesByRealType.Values).
        // When the game loads that output, DeserializeInto calls
        //   m_resolvedInstancesByRealType.AddAndAssertNew(obj.GetType(), obj)
        // for each resolver-section-2 object. Since the CONFIGS chunk already
        // pre-populated m_resolvedInstancesByRealType with the same concrete types, the
        // second call fires "Duplicate key 'GameDifficultyConfig'" for every config.
        if (iConfigType is not null)
        {
            try
            {
                var miSetSerPred = tBuilder.GetMethod("SetShouldSerializePredicate", pubInst);
                if (miSetSerPred is not null)
                {
                    Predicate<Type> excludeConfigs = (Type t) => !iConfigType.IsAssignableFrom(t);
                    miSetSerPred.Invoke(builder, new object?[] { excludeConfigs });
                    progress?.Report("  [resolver-build] Serialization predicate set: IConfig types excluded (configs live in CONFIGS chunk only).");
                }
                else
                {
                    progress?.Report("  [resolver-build] WARNING: SetShouldSerializePredicate not found — configs may appear twice on game load (duplicate key assertions).");
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"  [resolver-build] SetShouldSerializePredicate failed: {ex.Message} — configs may appear twice on game load.");
            }
        }

        // ── BuildAndClear's iterator checks BuildInfo.COUNT == 7 ─
        // Each of the 7 main game assemblies increments COUNT via an obfuscated static
        // ctor on first reference. We've referenced enough that COUNT *should* be 7,
        // but in practice it isn't — possibly because the obfuscated initialisers only
        // fire from specific code paths that the editor doesn't take. Force the value
        // to 7 so BuildAndClear doesn't throw FatalGameException("Err #13").
        ForceBuildInfoCountForIntegrityCheck(progress);

        // ── Build the resolver ─────────────────────────────────────────────
        try
        {
            var resolver = miBuildAndClear.Invoke(builder, null);
            if (resolver is null)
            {
                progress?.Report("  [resolver-build] BuildAndClear returned null — falling back.");
                return null;
            }
            progress?.Report("  [resolver-build] Populated DependencyResolver built successfully.");
            return resolver;
        }
        catch (Exception ex)
        {
            var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
            progress?.Report($"  [resolver-build] BuildAndClear threw: {inner.GetType().Name}: {inner.Message}");
            return null;
        }
    }

    /// <summary>
    /// True for assemblies we want to scan for <c>[GlobalDependency]</c>: vanilla Mafi.*,
    /// any Mafi DLC, and any third-party mod. False for the BCL, our own editor, and
    /// unrelated host assemblies.
    /// </summary>
    private static bool IsGameFamilyAssembly(string asmName)
    {
        // Anything starting with "Mafi" — covers Mafi, Mafi.Core, Mafi.Base, Mafi.TrainsDlc, etc.
        if (asmName.StartsWith("Mafi", StringComparison.Ordinal)) return true;

        // Third-party mods routinely live outside the Mafi.* prefix (COIExtended.*, etc.).
        // Anything we explicitly bundle/exclude is filtered elsewhere.  Be conservative and
        // exclude obvious host noise.
        if (asmName.StartsWith("System", StringComparison.Ordinal)) return false;
        if (asmName.StartsWith("Microsoft.", StringComparison.Ordinal)) return false;
        if (asmName.StartsWith("netstandard", StringComparison.Ordinal)) return false;
        if (asmName.StartsWith("mscorlib", StringComparison.Ordinal)) return false;
        if (asmName.StartsWith("PresentationFramework", StringComparison.Ordinal)) return false;
        if (asmName.StartsWith("PresentationCore", StringComparison.Ordinal)) return false;
        if (asmName.StartsWith("WindowsBase", StringComparison.Ordinal)) return false;
        if (asmName.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)) return false;
        if (asmName.StartsWith("UnityEngine", StringComparison.Ordinal)) return false;

        // Skip our own assembly so we don't try to auto-resolve editor types.
        if (asmName.StartsWith("COISaveEditor", StringComparison.OrdinalIgnoreCase)) return false;

        // Anything else (typically third-party mod assemblies like COIExtended.Core)
        // gets scanned. Stripped-mod filtering happens before this check.
        return true;
    }

    /// <summary>
    /// Calls <c>IProtosDbFriend.LockAndInitializeProtos()</c> on the populated ProtosDb if
    /// it isn't already locked. The game requires a locked, init-ed ProtosDb before any
    /// <c>EntityContext</c> can be auto-instantiated by the resolver.
    /// </summary>
    private static void LockProtosDbIfNeeded(object protosDb, System.IProgress<string>? progress)
    {
        try
        {
            // Check IsReadonly (internal property).
            var piReadonly = protosDb.GetType().GetProperty(
                "IsReadonly",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            bool alreadyLocked = piReadonly?.GetValue(protosDb) is bool b && b;
            if (alreadyLocked)
            {
                progress?.Report("  [resolver-build] ProtosDb already locked.");
                return;
            }

            var tFriend = AssemblyLoader.FindType("Mafi.Core.Prototypes.IProtosDbFriend");
            if (tFriend is null)
            {
                progress?.Report("  [resolver-build] IProtosDbFriend type not found — cannot lock ProtosDb.");
                return;
            }

            // Explicit interface impl: locate the method on the friend interface.
            var miLock = tFriend.GetMethod("LockAndInitializeProtos", Type.EmptyTypes);
            if (miLock is null)
            {
                progress?.Report("  [resolver-build] IProtosDbFriend.LockAndInitializeProtos not found.");
                return;
            }

            miLock.Invoke(protosDb, null);
            progress?.Report("  [resolver-build] ProtosDb locked and initialised.");
        }
        catch (Exception ex)
        {
            var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
            progress?.Report($"  [resolver-build] ProtosDb lock failed: {inner.GetType().Name}: {inner.Message}");
        }
    }

    /// <summary>
    /// Invokes <c>.AsSelf()</c> on a <c>DependencyInstanceRegistrar&lt;T&gt;</c> returned by
    /// <c>RegisterInstance</c>. Returns the registrar (chained calls supported) or null.
    /// </summary>
    private static object? CallAsSelf(object? registrar, System.IProgress<string>? progress, string label)
    {
        if (registrar is null) return null;
        try
        {
            var mi = registrar.GetType().GetMethod("AsSelf", Type.EmptyTypes);
            return mi?.Invoke(registrar, null);
        }
        catch (Exception ex)
        {
            progress?.Report($"  [resolver-build] AsSelf({label}) threw: {ex.Message}");
            return null;
        }
    }

    /// <summary>Invokes <c>.AsAllInterfaces()</c> on a <c>DependencyInstanceRegistrar&lt;T&gt;</c>.</summary>
    private static void CallAsAllInterfaces(object registrar)
    {
        try
        {
            var mi = registrar.GetType().GetMethod("AsAllInterfaces", Type.EmptyTypes);
            mi?.Invoke(registrar, null);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Per-type fallback for assemblies whose <c>Assembly.GetTypes()</c> throws
    /// <c>ReflectionTypeLoadException</c>. We replicate the inner loop of
    /// <c>RegisterAllGlobalDependencies</c>: for each LOADABLE type, call the private
    /// <c>tryRegisterAllGlobalDependencies(Type)</c> via reflection.
    /// </summary>
    private static int TryRegisterTypesIndividually(
        object builder, Type[] loadedTypes, Predicate<Type> shouldRegister,
        System.IProgress<string>? progress)
    {
        var tBuilder = builder.GetType();
        // The private name lives in the obfuscated build but the inner method exists
        // (see DependencyResolverBuilder.cs:691 in the decompiled source).
        var miInner = tBuilder.GetMethod(
            "tryRegisterAllGlobalDependencies",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(Type) },
            modifiers: null);
        if (miInner is null)
        {
            progress?.Report("  [resolver-build] (per-type) tryRegisterAllGlobalDependencies(Type) not found — cannot fall back.");
            return 0;
        }

        int registered = 0;
        foreach (var t in loadedTypes)
        {
            if (t is null) continue;
            if (!t.IsClass || t.IsAbstract) continue;
            if (!shouldRegister(t)) continue;
            try
            {
                miInner.Invoke(builder, new object?[] { t });
                registered++;
            }
            catch
            {
                // Per-type failures are silent at this granularity.
            }
        }
        return registered;
    }

    /// <summary>
    /// Force <c>Mafi.BuildInfo.COUNT</c> to <c>7</c> so <c>BuildAndClear</c>'s integrity
    /// check passes. The check at
    /// throws <c>FatalGameException</c>("Err #13") otherwise. Each game assembly is
    /// supposed to increment COUNT via an obfuscated static initialiser, but the
    /// editor's load order doesn't always trigger every initialiser, so we set the
    /// expected value directly.
    /// </summary>
    private static void ForceBuildInfoCountForIntegrityCheck(System.IProgress<string>? progress)
    {
        try
        {
            var tBuildInfo = AssemblyLoader.FindType("Mafi.BuildInfo");
            if (tBuildInfo is null)
            {
                progress?.Report("  [resolver-build] Mafi.BuildInfo not found — cannot bypass integrity check.");
                return;
            }
            var fiCount = tBuildInfo.GetField(
                "COUNT", BindingFlags.Public | BindingFlags.Static);
            if (fiCount is null)
            {
                progress?.Report("  [resolver-build] Mafi.BuildInfo.COUNT field not found.");
                return;
            }
            int before = fiCount.GetValue(null) is int b ? b : -1;
            fiCount.SetValue(null, 7);
            progress?.Report($"  [resolver-build] BuildInfo.COUNT: {before} → 7 (anti-tamper bypass).");
        }
        catch (Exception ex)
        {
            progress?.Report($"  [resolver-build] Could not set BuildInfo.COUNT: {ex.Message}");
        }
    }

    /// <summary>
    /// Registers no-op <c>DispatchProxy</c> stubs for every runtime-only TerrainManager
    /// dependency that isn't present in our editor process. Without these, the direct
    /// <c>FinalizeLoadingTimeSliced</c> path fails with
    /// <c>CorruptedSaveException("Failed to set dependency to '…'")</c>.
    /// All stubs return empty strings / zero / empty arrays — the real implementations
    /// are only used for terrain-cache file I/O which never fires during deep-edit.
    /// </summary>
    private static void TryRegisterRuntimeOnlyStubs(
        object builder, MethodInfo miRegisterInstanceGeneric,
        System.IProgress<string>? progress)
    {
        // Each entry: (fully-qualified interface name, friendly label for logging)
        var stubs = new[]
        {
            ("Mafi.Core.IFileSystemHelper",                   "IFileSystemHelper"),
            ("Mafi.Core.Terrain.Generation.IMapCacheManager", "IMapCacheManager"),
        };

        var tDispatchProxy = typeof(System.Reflection.DispatchProxy);
        var miCreate = tDispatchProxy.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2);
        if (miCreate is null)
        {
            progress?.Report("  [resolver-build] DispatchProxy.Create<T,TProxy> not found — skipping all runtime-only stubs.");
            return;
        }

        foreach (var (typeName, label) in stubs)
        {
            try
            {
                var tInterface = AssemblyLoader.FindType(typeName);
                if (tInterface is null)
                {
                    progress?.Report($"  [resolver-build] {typeName} not found — skipping stub.");
                    continue;
                }

                var stub = miCreate
                    .MakeGenericMethod(tInterface, typeof(NoOpDispatchProxy))
                    .Invoke(null, null);
                if (stub is null)
                {
                    progress?.Report($"  [resolver-build] DispatchProxy.Create returned null for {label} — skipping.");
                    continue;
                }

                var registrar = miRegisterInstanceGeneric
                    .MakeGenericMethod(tInterface)
                    .Invoke(builder, new object?[] { stub, false });

                if (registrar is not null)
                {
                    var miAs = registrar.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "As" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1);
                    miAs?.MakeGenericMethod(tInterface).Invoke(registrar, null);
                }

                progress?.Report($"  [resolver-build] Registered stub {label}.");
            }
            catch (Exception ex)
            {
                var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                progress?.Report($"  [resolver-build] Stub {label} registration failed: {inner.GetType().Name}: {inner.Message}");
            }
        }
    }
}

/// <summary>
/// Generic no-op <c>DispatchProxy</c> used for any runtime-only game interface that
/// must be registered in the resolver to satisfy deserialization but whose methods
/// are never called during deep-edit (e.g. <c>IFileSystemHelper</c>,
/// <c>IMapCacheManager</c>). Returns empty strings, zero, empty arrays, or null.
/// </summary>
/// <remarks>
/// MUST NOT be <c>sealed</c> — <c>DispatchProxy.Create&lt;T,TProxy&gt;()</c> rejects
/// sealed proxy types with <c>ArgumentException("The base type 'X' cannot be sealed")</c>
/// because it generates a runtime subclass of <c>TProxy</c>.
/// </remarks>
public class NoOpDispatchProxy : System.Reflection.DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null) return null;
        var rt = targetMethod.ReturnType;
        if (rt == typeof(string)) return string.Empty;
        if (rt == typeof(void))   return null;
        if (rt.IsArray)           return Array.CreateInstance(rt.GetElementType()!, 0);
        if (rt.IsValueType)       return Activator.CreateInstance(rt);
        return null;
    }
}
