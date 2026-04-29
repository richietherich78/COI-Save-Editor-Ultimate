using System.Reflection;

namespace COISaveEditorUltimate.DeepEdit;

public sealed partial class DeepEditEngine
{
    // ── Resolver diagnostics ──────────────────────────────────────────────

    /// <summary>
    /// Logs the count of entries in the resolver's registered-type dict and
    /// checks whether ProtosDb and IFileSystemHelper are actually findable.
    /// Called immediately before Phase 2 to diagnose injection failures.
    /// </summary>
    private void DiagnoseResolverState(object resolver, IProgress<string>? progress)
    {
        try
        {
            var resolverType = resolver.GetType();
            var fiByReg = FindFieldDeep(resolverType, "m_resolvedInstancesByRegisteredType");
            var fiByReal = FindFieldDeep(resolverType, "m_resolvedInstancesByRealType");
            var fiObjs = FindFieldDeep(resolverType, "m_resolvedObjects");

            var dictReg = fiByReg?.GetValue(resolver);
            var dictReal = fiByReal?.GetValue(resolver);
            var lystObjs = fiObjs?.GetValue(resolver);

            int countReg  = (int?)dictReg?.GetType().GetProperty("Count")?.GetValue(dictReg) ?? -1;
            int countReal = (int?)dictReal?.GetType().GetProperty("Count")?.GetValue(dictReal) ?? -1;
            int countObjs = (int?)lystObjs?.GetType().GetProperty("Count")?.GetValue(lystObjs) ?? -1;
            progress?.Report($"  PRE-PHASE2 DIAG: byRegisteredType.Count={countReg}, byRealType.Count={countReal}, resolvedObjects.Count={countObjs}");

            void CheckType(object? dict, string typeName, string label)
            {
                if (dict is null) return;
                var tTarget = AssemblyLoader.FindType(typeName);
                if (tTarget is null) { progress?.Report($"    {label}: type not found in loaded assemblies"); return; }
                var miTryGet = dict.GetType().GetMethod("TryGetValue",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(Type), typeof(object).MakeByRefType() }, null);
                if (miTryGet is null) { progress?.Report($"    {label}: TryGetValue method not found on dict"); return; }
                object?[] args = { tTarget, null };
                bool found = (bool)miTryGet.Invoke(dict, args)!;
                progress?.Report($"    {label}: found={found}, value={args[1]?.GetType().Name ?? "null"}");
            }

            CheckType(dictReg,  "Mafi.Core.Prototypes.ProtosDb",              "byReg[ProtosDb]");
            CheckType(dictReal, "Mafi.Core.Prototypes.ProtosDb",              "byReal[ProtosDb]");
            CheckType(dictReg,  "Mafi.Core.IFileSystemHelper",                "byReg[IFileSystemHelper]");
            CheckType(dictReg,  "Mafi.Core.Terrain.Generation.IMapCacheManager", "byReg[IMapCacheManager]");
        }
        catch (Exception ex)
        {
            progress?.Report($"  PRE-PHASE2 DIAG failed: {ex.Message}");
        }
    }

    // ── Pre-serialisation fixups ──────────────────────────────────────────

    /// <summary>
    /// Fix up objects whose Phase 3 (InitAfterLoad) couldn't fully complete.
    /// </summary>
    private void PrepareObjectsForReserialization(object resolver, object reader, IProgress<string>? progress)
    {
        var resolverType = resolver.GetType();

        // DependencyResolver.Serialize writes from THREE collections:
        //   m_resolvedObjects, m_resolvedInstancesByRealType.Values,
        //   m_resolvedInstancesByRegisteredType.Values
        // We must cover all three so every queued object is pre-fixed.
        // Use a reference-equality set to avoid processing duplicates.
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var allSources = new List<System.Collections.IEnumerable?>();
        allSources.Add(FindFieldDeep(resolverType, "m_resolvedObjects")?.GetValue(resolver) as System.Collections.IEnumerable);

        var fiByReal = FindFieldDeep(resolverType, "m_resolvedInstancesByRealType");
        var dictByReal = fiByReal?.GetValue(resolver);
        if (dictByReal is not null)
        {
            var miValues = dictByReal.GetType().GetProperty("Values",
                BindingFlags.Public | BindingFlags.Instance);
            allSources.Add(miValues?.GetValue(dictByReal) as System.Collections.IEnumerable);
        }

        var fiByReg = FindFieldDeep(resolverType, "m_resolvedInstancesByRegisteredType");
        var dictByReg = fiByReg?.GetValue(resolver);
        if (dictByReg is not null)
        {
            var miValues = dictByReg.GetType().GetProperty("Values",
                BindingFlags.Public | BindingFlags.Instance);
            allSources.Add(miValues?.GetValue(dictByReg) as System.Collections.IEnumerable);
        }

        var tTerrainManager          = AssemblyLoader.FindType("Mafi.Core.Terrain.TerrainManager");
        var tUnlockedProtosDb        = AssemblyLoader.FindType("Mafi.Core.Prototypes.UnlockedProtosDb");
        var tClearancePathability    = AssemblyLoader.FindType("Mafi.Core.PathFinding.ClearancePathabilityProvider");
        var tModJsonConfig           = AssemblyLoader.FindType("Mafi.Core.Mods.ModJsonConfig");

        if (tTerrainManager is null)
            progress?.Report("    Could not locate TerrainManager type — skipping terrain fixup.");
        if (tUnlockedProtosDb is null)
            progress?.Report("    Could not locate UnlockedProtosDb type — skipping unlock fixup.");
        if (tClearancePathability is null)
            progress?.Report("    Could not locate ClearancePathabilityProvider — skipping pathfinding fixup.");
        if (tModJsonConfig is null)
            progress?.Report("    Could not locate ModJsonConfig — skipping config fixup.");

        foreach (var source in allSources)
        {
            if (source is null) continue;
            foreach (var obj in source)
            {
                if (obj is null || !visited.Add(obj)) continue;
                var objType = obj.GetType();

                if (tTerrainManager is not null && objType == tTerrainManager)
                    ReconstitutTerrainDataArrays(obj, progress);

                if (tUnlockedProtosDb is not null && tUnlockedProtosDb.IsAssignableFrom(objType))
                    RunUnlockedProtosDbInitAfterLoad(obj, progress);

                if (tClearancePathability is not null && tClearancePathability.IsAssignableFrom(objType))
                    InitClearancePathabilityProviderData(obj, progress);

                if (tModJsonConfig is not null && tModJsonConfig.IsAssignableFrom(objType))
                    InitModJsonConfigParameters(obj, progress);
            }
        }

        // Run Dict<,>.initAfterLoad for all deserialized Dict instances registered in
        // the reader's pending-init list. Phase 3 is skipped to prevent manager-side-effect
        // callbacks from corrupting the save, but Dict.initAfterLoad is pure bucket-setup
        // (no manager writes). Without it, any Dict with m_entries non-null and m_buckets
        // null will throw "Trying to enumerate a dict that was not initialized after load."
        // during RESOLVER FinalizeSerialization.
        RunDictInitAfterLoadCallbacks(reader, progress);
    }

    /// <summary>
    /// Runs UnlockedProtosDb.initAfterLoad() via reflection so that proto IsUnlocked
    /// states are properly set before re-serialisation. Phase 3 is skipped in our tool
    /// to avoid expensive game-side callbacks; we call this one method explicitly because
    /// it only sets IsUnlocked flags (safe, no manager side-effects) and without it the
    /// serialized UnlockedProtosForSerialization list would be empty, causing all
    /// research-gated protos to appear locked when the game loads the produced save.
    /// </summary>
    private static void RunUnlockedProtosDbInitAfterLoad(object unlockedProtosDb, IProgress<string>? progress)
    {
        try
        {
            // Verify m_protosDb was set by Phase 2 (if null, skip — same NRE we're fixing)
            var fiProtos = FindFieldDeep(unlockedProtosDb.GetType(), "m_protosDb");
            if (fiProtos is null || fiProtos.GetValue(unlockedProtosDb) is null)
            {
                progress?.Report("    UnlockedProtosDb.initAfterLoad skipped — m_protosDb still null.");
                return;
            }

            var mi = FindMethodDeep(unlockedProtosDb.GetType(), "initAfterLoad");
            if (mi is null)
            {
                progress?.Report("    UnlockedProtosDb.initAfterLoad not found — skipping unlock fixup.");
                return;
            }

            mi.Invoke(unlockedProtosDb, null);
            progress?.Report("    UnlockedProtosDb.initAfterLoad invoked — proto IsUnlocked states set.");
        }
        catch (Exception ex)
        {
            var inner = (ex as System.Reflection.TargetInvocationException)?.InnerException ?? ex;
            progress?.Report($"    UnlockedProtosDb.initAfterLoad threw: {inner.GetType().Name}: {inner.Message}");
        }
    }

    /// <summary>
    /// Runs initAfterLoad() for every Mafi.Collections type (Dict, Set, Lyst, etc.) registered
    /// in the BlobReader's pending-init list. Phase 3 is skipped for safety, but collection
    /// initAfterLoad is pure bucket/slot initialization — no side effects on game managers.
    /// Without it, deserialized collections with m_entries non-null but m_buckets null throw
    /// "not initialized after load" during RESOLVER FinalizeSerialization.
    /// </summary>
    private void RunDictInitAfterLoadCallbacks(object reader, IProgress<string>? progress)
    {
        try
        {
            if (_tBlobReader is null) return;
            var fiInit = _tBlobReader.GetField("m_objsToInit",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (fiInit is null) return;

            var initList = fiInit.GetValue(reader) as System.Collections.IEnumerable;
            if (initList is null) return;

            int dictCount = 0, dictFail = 0;
            foreach (var initData in initList)
            {
                if (initData is null) continue;
                var initType = initData.GetType();
                var obj = initType.GetField("Obj", BindingFlags.Public | BindingFlags.Instance)?.GetValue(initData);
                if (obj is null) continue;

                // Only process Mafi.Collections types (Dict, Set, Lyst, etc.).
                var objTypeName = obj.GetType().FullName ?? "";
                if (!objTypeName.StartsWith("Mafi.Collections.", StringComparison.Ordinal))
                    continue;

                // Check IsInitializedAfterLoad: skip if already good.
                var piInit = obj.GetType().GetProperty("IsInitializedAfterLoad",
                    BindingFlags.Public | BindingFlags.Instance);
                if (piInit?.GetValue(obj) is true)
                    continue;

                var mi = FindMethodDeep(obj.GetType(), "initAfterLoad");
                if (mi is null) continue;

                try
                {
                    mi.Invoke(obj, null);
                    dictCount++;
                }
                catch (Exception ex)
                {
                    var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                    progress?.Report($"    Dict initAfterLoad threw: {inner.GetType().Name}: {inner.Message}");
                    dictFail++;
                }
            }

            if (dictCount > 0 || dictFail > 0)
                progress?.Report($"    Dict.initAfterLoad: {dictCount} initialized, {dictFail} failed.");
        }
        catch (Exception ex)
        {
            progress?.Report($"    RunDictInitAfterLoadCallbacks error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// ModJsonConfig.m_parameters is a readonly field set by the constructor and mod-loading init,
    /// never by DeserializeData. Without initAfterLoad (Phase 3 skipped), it stays null and
    /// SerializeData throws NRE at writer.WriteInt(m_parameters.Count). We initialize it to an
    /// empty dict so serialization succeeds; config values are stored in the CONFIGS chunk anyway.
    /// </summary>
    private static void InitModJsonConfigParameters(object modJsonConfig, IProgress<string>? progress)
    {
        try
        {
            var modIdProp = modJsonConfig.GetType().GetProperty("ModId",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            string modId = modIdProp?.GetValue(modJsonConfig) as string ?? "<unknown>";

            var fi = FindFieldDeep(modJsonConfig.GetType(), "m_parameters");
            if (fi is null)
            {
                progress?.Report($"    ModJsonConfig({modId}): m_parameters field not found.");
                return;
            }

            var current = fi.GetValue(modJsonConfig);
            if (current is not null)
            {
                // Already set — check for null issues via a dry-run property access.
                int count = -1;
                try { count = (int)current.GetType().GetProperty("Count")!.GetValue(current)!; } catch { }
                progress?.Report($"    ModJsonConfig({modId}): m_parameters already set (Count={count}).");
                return;
            }

            var emptyDict = Activator.CreateInstance(fi.FieldType);
            fi.SetValue(modJsonConfig, emptyDict);
            progress?.Report($"    ModJsonConfig({modId}): m_parameters initialized (empty).");
        }
        catch (Exception ex)
        {
            var inner = (ex as System.Reflection.TargetInvocationException)?.InnerException ?? ex;
            progress?.Report($"    ModJsonConfig init threw: {inner.GetType().Name}: {inner.Message}");
        }
    }

    /// <summary>
    /// ClearancePathabilityProvider.m_data is set by initSelfVeryHigh (Phase 3 Highest), which
    /// we skip. Without it, the m_initializedChunks getter throws ArgumentNullException during
    /// RESOLVER re-serialization. We initialize m_data to an empty array so the getter can run.
    /// The game recomputes pathfinding chunk data from scratch on load anyway.
    /// </summary>
    private static void InitClearancePathabilityProviderData(object provider, IProgress<string>? progress)
    {
        try
        {
            var fiData = FindFieldDeep(provider.GetType(), "m_data");
            if (fiData is null)
            {
                progress?.Report("    WARN: m_data not found on ClearancePathabilityProvider.");
                return;
            }

            if (fiData.GetValue(provider) is not null)
                return; // already initialized

            // Read Chunk8TotalCount from the TerrainManager reference held by this provider.
            var piTerrain = provider.GetType().GetProperty("TerrainManager",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            object? tm = piTerrain?.GetValue(provider);
            int chunkCount = 0;
            if (tm is not null)
            {
                var piCount = tm.GetType().GetProperty("Chunk8TotalCount",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                chunkCount = piCount?.GetValue(tm) is int c ? c : 0;
            }

            // Create Option<DataChunk>[] of the right length (all entries default to None).
            var elementType = fiData.FieldType.GetElementType()!;
            var emptyArray = Array.CreateInstance(elementType, chunkCount);
            fiData.SetValue(provider, emptyArray);
            progress?.Report($"    ClearancePathabilityProvider.m_data initialized (length={chunkCount}).");
        }
        catch (Exception ex)
        {
            var inner = (ex as System.Reflection.TargetInvocationException)?.InnerException ?? ex;
            progress?.Report($"    ClearancePathabilityProvider init threw: {inner.GetType().Name}: {inner.Message}");
        }
    }

    /// <summary>
    /// Reconstitutes TerrainData arrays from LoadedData so re-serialisation works.
    /// </summary>
    private void ReconstitutTerrainDataArrays(object terrainManager, IProgress<string>? progress)
    {
        try
        {
            var tmType = terrainManager.GetType();
            var fiData = FindFieldDeep(tmType, "m_data");
            if (fiData is null) { progress?.Report("    WARN: m_data not found on TerrainManager."); return; }

            object oldData = fiData.GetValue(terrainManager)!;
            var dataType = oldData.GetType();
            var allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            if (dataType.GetField("Heights")!.GetValue(oldData) is not null)
            {
                progress?.Report("    TerrainData arrays already allocated — no fixup needed.");
                return;
            }

            int width  = (int)dataType.GetField("Width")!.GetValue(oldData)!;
            int height = (int)dataType.GetField("Height")!.GetValue(oldData)!;
            int total  = width * height;

            var fiLoadedData     = dataType.GetField("LoadedData", allFlags);
            var fiLoadedOverflow = dataType.GetField("LoadedOverflowData", allFlags);
            if (fiLoadedData is null || fiLoadedOverflow is null)
            {
                progress?.Report("    WARN: Could not find LoadedData/LoadedOverflowData fields on TerrainData.");
                return;
            }
            object loadedData     = fiLoadedData.GetValue(oldData)!;
            object loadedOverflow = fiLoadedOverflow.GetValue(oldData)!;

            int loadedCount = (int)loadedData.GetType().GetProperty("Count")!.GetValue(loadedData)!;
            progress?.Report($"    Reconstituting TerrainData {width}×{height} ({loadedCount} changed tiles)…");

            if (loadedCount == 0)
            {
                progress?.Report("    WARN: LoadedData is empty — no tiles to reconstitute.");
                return;
            }

            object newData = Activator.CreateInstance(dataType, new object[] { width, height, false })!;

            dataType.GetField("SavedFlagsMask")!.SetValue(newData, (uint)0xFFFF);
            dataType.GetField("MaterialLayersOverflow")!.SetValue(newData, loadedOverflow);

            Array heightsArr   = (Array)dataType.GetField("Heights")!.GetValue(newData)!;
            Array surfacesArr  = (Array)dataType.GetField("Surfaces")!.GetValue(newData)!;
            Array flagsArr     = (Array)dataType.GetField("Flags")!.GetValue(newData)!;
            Array materialsArr = (Array)dataType.GetField("MaterialLayers")!.GetValue(newData)!;

            object changedBitmap = dataType.GetField("ChangedTiles")!.GetValue(newData)!;
            var piBacking = changedBitmap.GetType().GetProperty("BackingArray");
            ulong[] changedBits = (ulong[])(piBacking!.GetValue(changedBitmap)!);

            var miGetBacking = loadedData.GetType().GetMethod("GetBackingArray")!;
            Array backing = (Array)miGetBacking.Invoke(loadedData, null)!;

            Type? pairType = null, ltdType = null, tileIdxType = null;
            FieldInfo? fFirst = null, fSecond = null, fValue = null;
            FieldInfo? fHeight = null, fSurface = null, fFlags = null, fLayers = null;

            for (int i = 0; i < loadedCount; i++)
            {
                object pair = backing.GetValue(i)!;
                if (pairType is null)
                {
                    pairType   = pair.GetType();
                    fFirst     = pairType.GetField("First")!;
                    fSecond    = pairType.GetField("Second")!;
                }

                object tileIdx  = fFirst!.GetValue(pair)!;
                object tileData = fSecond!.GetValue(pair)!;

                if (tileIdxType is null)
                {
                    tileIdxType = tileIdx.GetType();
                    fValue      = tileIdxType.GetField("Value")!;
                }
                if (ltdType is null)
                {
                    ltdType  = tileData.GetType();
                    fHeight  = ltdType.GetField("Height")!;
                    fSurface = ltdType.GetField("Surface")!;
                    fFlags   = ltdType.GetField("Flags")!;
                    fLayers  = ltdType.GetField("Layers")!;
                }

                int idx = (int)fValue!.GetValue(tileIdx)!;

                heightsArr.SetValue(fHeight!.GetValue(tileData)!, idx);
                surfacesArr.SetValue(fSurface!.GetValue(tileData)!, idx);
                flagsArr.SetValue(fFlags!.GetValue(tileData)!, idx);
                materialsArr.SetValue(fLayers!.GetValue(tileData)!, idx);

                changedBits[idx >> 6] |= 1UL << (idx & 63);
            }

            fiData.SetValue(terrainManager, newData);
            progress?.Report($"    TerrainData reconstituted — {loadedCount} tiles applied.");
        }
        catch (Exception ex)
        {
            progress?.Report($"    WARN: TerrainData reconstitution failed: {ex.GetType().Name}: {ex.Message}");
            progress?.Report($"      at: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
        }
    }

    /// <summary>
    /// Injects our populated ProtosDb into the resolver's type registries.
    /// </summary>
    private void InjectProtosDbIntoResolver(object resolver, object protosDb, IProgress<string>? progress)
    {
        try
        {
            var resolverType = resolver.GetType();
            var tProtosDb = protosDb.GetType();

            var fiByReal = FindFieldDeep(resolverType, "m_resolvedInstancesByRealType");
            if (fiByReal is not null)
            {
                var dict = fiByReal.GetValue(resolver);
                if (dict is not null)
                {
                    SetDictEntry(dict, tProtosDb, protosDb, progress, "byRealType");
                }
            }

            var fiByReg = FindFieldDeep(resolverType, "m_resolvedInstancesByRegisteredType");
            if (fiByReg is not null)
            {
                var dict = fiByReg.GetValue(resolver);
                if (dict is not null)
                {
                    SetDictEntry(dict, tProtosDb, protosDb, progress, "byRegisteredType");
                    var tInterface = AssemblyLoader.FindType("Mafi.Core.Prototypes.IProtosDbFriend");
                    if (tInterface is not null)
                        SetDictEntry(dict, tInterface, protosDb, progress, null);
                }
            }

            var fiObjs = FindFieldDeep(resolverType, "m_resolvedObjects");
            if (fiObjs is not null)
            {
                var lyst = fiObjs.GetValue(resolver);
                if (lyst is not null)
                {
                    var miAdd = lyst.GetType().GetMethod("Add", new[] { typeof(object) })
                        ?? lyst.GetType().GetMethod("Add");
                    miAdd?.Invoke(lyst, new[] { protosDb });
                }
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"    Note: ProtosDb injection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets dict[key] = value using the most reliable reflection path available, with
    /// diagnostic read-back to verify the write actually took effect.
    /// </summary>
    private static void SetDictEntry(object dict, Type key, object value,
                                     IProgress<string>? progress, string? label)
    {
        var dictType = dict.GetType();

        // Preferred path: call the typed Add(TKey, TValue) method which takes two args and
        // calls insert(skipIfExists:false) — overwrites duplicates in release builds.
        // Using explicit parameter types avoids the AmbiguousMatchException that
        // GetProperty("Item") can throw when both the typed and IDictNonGeneric indexers
        // are visible to reflection.
        bool wrote = false;
        try
        {
            var miAdd = dictType.GetMethod("Add",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(Type), typeof(object) },
                null);
            if (miAdd != null)
            {
                miAdd.Invoke(dict, new object[] { key, value });
                wrote = true;
            }
        }
        catch { /* duplicate key – overwrite below */ }

        if (!wrote)
        {
            // Fallback: use GetProperty with explicit parameter to disambiguate.
            var indexer = dictType.GetProperty("Item",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                typeof(object),                    // TValue = object
                new[] { typeof(Type) },            // TKey  = Type
                null);
            indexer?.SetValue(dict, value, new object[] { key });
            wrote = indexer != null;
        }

        // Verify the write by reading back.
        if (label != null)
        {
            try
            {
                var miTryGet = dictType.GetMethod("TryGetValue",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(Type), typeof(object).MakeByRefType() },
                    null);
                object?[] args = { key, null };
                bool found = miTryGet != null && (bool)miTryGet.Invoke(dict, args)!;
                string readBack = found ? (args[1]?.GetType().Name ?? "null") : "NOT FOUND";
                progress?.Report($"    Injected ProtosDb into resolver ({label}), verify read-back: {readBack}.");
            }
            catch (Exception ex)
            {
                progress?.Report($"    Injected ProtosDb into resolver ({label}), verify failed: {ex.Message}.");
            }
        }
    }

    /// <summary>
    /// Scans all SlimIdManagers in the resolver and records which phantom stubs live in their
    /// ManagedProtos arrays into <see cref="_stubsInSlimIdManagers"/>.
    /// Must be called after AuditPhantomProtoRefs (so stubs are still identifiable) and before
    /// NullifyPhantomProtoIds (which uses the collected set to skip ID hijacking).
    /// <para/>
    /// Rationale: SlimIdManagerBase.initAfterLoad checks each saved ManagedProtos entry against
    /// vanilla ProtosDb.  If we hijack "AlnicoIngot" → "IronOre", the game resolves that slot to
    /// the real IronOre proto and removes it from the vanilla set.  When initAfterLoad later
    /// reaches the real IronOre position it is already gone → "Missing proto detected" → replaced
    /// with PhantomProto → IronOre gets a brand-new appended SlimId → ProductStats[oldSlimId]
    /// mismatch → "Products changed after load" → all vanilla storage stats show 0.
    /// Keeping the original COIExtended ID ("AlnicoIngot") causes the game's ReadWeakProtoRef to
    /// produce PhantomProto for that unknown ID; initAfterLoad then skips phantom slots, leaving
    /// every vanilla proto at its correct SlimId position and preserving ProductStats.
    /// </summary>
    private void CollectSlimIdManagerStubs(object resolver, IProgress<string>? progress = null)
    {
        if (_phantomProtoStubs is null || _phantomProtoStubs.Count == 0) return;

        _stubsInSlimIdManagers = new HashSet<object>(ReferenceEqualityComparer.Instance);
        _slimIdProtectedProtoIdStrings = new HashSet<string>(StringComparer.Ordinal);

        // Needed to extract proto ID strings for _slimIdProtectedProtoIdStrings.
        var tProtoForIds = AssemblyLoader.FindType("Mafi.Core.Prototypes.Proto");
        var fiProtoIdForIds = tProtoForIds?.GetField("<Id>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

        const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Phase A: collect SlimIdManager instances.
        // Critical: SlimIdManagers are NOT top-level resolver objects — they are FIELDS on
        // manager classes (e.g. ProductsManager.<SlimIdManager>k__BackingField).
        // CollectAllResolverObjects returns only the top-level objects, so we scan one level
        // deeper: for each resolver object, check all its fields whose DECLARED TYPE contains
        // "SlimIdManager" in the name.
        var slimManagers = new List<object>();
        var allObjects = CollectAllResolverObjects(resolver);
        foreach (var obj in allObjects)
        {
            if (obj is null) continue;
            // Check if the object IS a SlimIdManager (belt-and-braces; unlikely top-level).
            if (obj.GetType().Name.Contains("SlimIdManager"))
            {
                slimManagers.Add(obj);
                continue;
            }
            // Scan declared fields for SlimIdManager-typed values.
            for (var t = obj.GetType(); t is not null && t != typeof(object); t = t.BaseType)
            {
                foreach (var fi in t.GetFields(allInst | BindingFlags.DeclaredOnly))
                {
                    if (!fi.FieldType.Name.Contains("SlimIdManager")) continue;
                    try
                    {
                        var val = fi.GetValue(obj);
                        if (val is not null) slimManagers.Add(val);
                    }
                    catch { }
                }
            }
        }

        if (slimManagers.Count == 0)
        {
            progress?.Report("  WARNING: No SlimIdManagers found in resolver — storage-stats fix cannot proceed.");
            return;
        }

        // Phase B: for each SlimIdManager enumerate ManagedProtos.
        // ManagedProtos is ImmutableArray<TProto> — a STRUCT that does not implement
        // IEnumerable but exposes a struct-based GetEnumerator().  Reuse AuditTryEnumerate
        // (same logic as the AUDIT pass) which calls GetEnumerator/MoveNext/Current via
        // reflection — this avoids depending on the backing field name (which the decompiler
        // shows as "m_items" but may be obfuscated in the compiled DLL).
        int stubsFound = 0;
        foreach (var slimMgr in slimManagers)
        {
            var fiManaged = FindFieldDeep(slimMgr.GetType(), "ManagedProtos");
            if (fiManaged is null) continue;

            try
            {
                var boxedImmArr = fiManaged.GetValue(slimMgr);
                if (boxedImmArr is null) continue;

                // AuditTryEnumerate handles both IEnumerable and struct-based GetEnumerator().
                var items = AuditTryEnumerate(boxedImmArr);
                if (items is null) continue;

                foreach (var item in items)
                {
                    if (item is not null && _phantomProtoStubs.Contains(item))
                    {
                        _stubsInSlimIdManagers.Add(item);
                        stubsFound++;

                        // Record proto ID string so NullifyPhantomProtoIds can also protect "sibling"
                        // stubs (e.g. ProductStats[i].Product) that share the same ID but are different
                        // objects because the factory creates a fresh stub per deserialization call.
                        if (fiProtoIdForIds is not null)
                        {
                            try
                            {
                                var idObj = fiProtoIdForIds.GetValue(item);
                                if (idObj is not null)
                                {
                                    var vp = idObj.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                                    var idStr = (vp?.GetValue(idObj) as string) ?? idObj.ToString();
                                    if (!string.IsNullOrEmpty(idStr))
                                        _slimIdProtectedProtoIdStrings.Add(idStr);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        var typeNames = slimManagers.Select(m => m.GetType().Name).Distinct().Take(8);
        progress?.Report($"  SlimIdManagers found: {slimManagers.Count} ({string.Join(", ", typeNames)}), phantom stubs in ManagedProtos: {stubsFound}.");
    }

    /// <summary>
    /// Adjusts phantom proto IDs before re-serialisation.
    /// Core game protos (Mafi.Core, Mafi.Base, Mafi.TrainsDlc) keep their original save-file IDs
    /// so the game's own ProtosDb can resolve them on load.
    /// Protos from removed mods get unique placeholder IDs to avoid duplicate-key exceptions
    /// in the game's SlimIdManager.
    /// Stubs that live inside SlimIdManager.ManagedProtos arrays are intentionally left with their
    /// original mod IDs — the game's ReadWeakProtoRef will produce PhantomProto for unknown IDs,
    /// and initAfterLoad skips phantom entries, keeping vanilla protos at their correct SlimId
    /// positions and preserving all saved ProductStats.
    /// </summary>
    private void NullifyPhantomProtoIds(IProgress<string>? progress)
    {
        if (_phantomProtoStubs is null || _phantomProtoStubs.Count == 0)
        {
            progress?.Report("  No phantom proto stubs to clean.");
            return;
        }

        // _stubsInSlimIdManagers was populated by CollectSlimIdManagerStubs (called after AuditPhantomProtoRefs).
        // These stubs must NOT be hijacked — see CollectSlimIdManagerStubs for the full rationale.
        var stubsInManagedProtos = _stubsInSlimIdManagers ?? new HashSet<object>(ReferenceEqualityComparer.Instance);
        progress?.Report($"  SlimIdManager stubs excluded from hijacking: {stubsInManagedProtos.Count} " +
            $"(keep original COIExtended IDs — game's ReadWeakProtoRef returns PhantomProto for unknown IDs).");

        var tProto = AssemblyLoader.FindType("Mafi.Core.Prototypes.Proto");
        var fiProtoId = tProto?.GetField("<Id>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        var tProtoID = tProto?.GetNestedType("ID", BindingFlags.Public);
        var ctorProtoID = tProtoID?.GetConstructor(new[] { typeof(string) });

        if (fiProtoId is null || ctorProtoID is null)
        {
            progress?.Report($"  WARNING: Cannot process phantom proto IDs (reflection failed). " +
                $"{_phantomProtoStubs.Count} phantom stubs will be written with original IDs.");
            return;
        }

        // Assemblies whose protos the game ships — the game will resolve these IDs itself.
        var gameAssemblyPrefixes = GameAssemblyPrefixes;

        // Track IDs we've already seen to ensure uniqueness (game's SlimIdManager requires it).
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        int keptOriginal = 0;
        int reassigned = 0;
        int hijackedToVanilla = 0;
        int phantomIndex = 0;

        int skippedInManagedProtos = 0;
        foreach (var stub in _phantomProtoStubs)
        {
            try
            {
                // Stubs in SlimIdManager.ManagedProtos must keep their original mod IDs.
                // The game's ReadWeakProtoRef returns PhantomProto for unknown IDs; initAfterLoad
                // then skips phantom slots, preserving vanilla protos at their correct positions.
                if (stubsInManagedProtos.Contains(stub))
                {
                    skippedInManagedProtos++;
                    continue;
                }

                string? originalId = null;
                var idObj = fiProtoId.GetValue(stub);
                if (idObj is not null)
                {
                    // Proto.ID has a ToString() or Value property that gives the string ID.
                    var valueProp = idObj.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    originalId = (valueProp?.GetValue(idObj) as string) ?? idObj.ToString();
                }

                // Also protect stubs whose ID string matches a ManagedProtos stub ID.
                // The factory creates a DIFFERENT stub object for each field that holds the same proto
                // (e.g. ManagedProtos[i] and ProductStats[i].Product both read "AlnicoIngot" but get
                // separate stub objects).  The reference check above misses the ProductStats sibling;
                // the ID-string check catches it so both stay at the original mod ID → both become
                // PhantomProto on game load → SlimId-indexed arrays stay aligned.
                if (_slimIdProtectedProtoIdStrings is not null
                    && !string.IsNullOrEmpty(originalId)
                    && _slimIdProtectedProtoIdStrings.Contains(originalId!))
                {
                    skippedInManagedProtos++;
                    continue;
                }

                bool isGameProto = IsGameAssemblyType(stub.GetType(), gameAssemblyPrefixes);

                // A proto whose C# type comes from a game assembly but whose ID was registered
                // by a mod (e.g. COIExtended's "Cannery" stored as plain MachineProto) would
                // pass the isGameProto check yet the vanilla game cannot resolve its ID.
                // Cross-check against our populated ProtosDb: if the ID isn't there, the vanilla
                // game won't find it either, so treat it like a mod proto and assign a placeholder.
                bool isInVanillaDb = isGameProto
                    && !string.IsNullOrEmpty(originalId)
                    && _protoHealingLookup?.ById.ContainsKey(originalId!) == true;

                if (isInVanillaDb && seenIds.Add(originalId!))
                {
                    // Keep original ID — game will resolve it from its own ProtosDb.
                    keptOriginal++;
                    continue;
                }

                // Try to "hijack" the phantom by reassigning it the ID of any existing
                // vanilla proto of the same exact concrete type. The game will then load
                // the slot as that vanilla proto. Slim-ID indexing works, typed casts
                // succeed. The "deleted" semantics are slightly weird (multiple slim-IDs
                // resolve to the same real proto) but this is a tool for rescuing a save
                // by removing a mod, not for preserving exact game state — it's the
                // least-bad outcome compared to the game crashing on load.
                if (TryHijackToVanillaIdOfSameType(stub, fiProtoId, ctorProtoID, seenIds))
                {
                    hijackedToVanilla++;
                    continue;
                }

                // No vanilla proto of compatible type exists — assign a unique placeholder.
                // This will likely crash the game on load if anything references this slot,
                // but the validator will surface that for the user.
                string phantomId;
                do
                {
                    phantomId = $"__phantom_{phantomIndex++}";
                } while (!seenIds.Add(phantomId));

                var uniqueId = ctorProtoID.Invoke(new object[] { phantomId })!;
                fiProtoId.SetValue(stub, uniqueId);
                reassigned++;
                if (reassigned <= 25)
                    progress?.Report($"    Placeholder '{phantomId}' assigned: stub type {stub.GetType().FullName} (originalId='{originalId ?? "<null>"}') — no vanilla candidate in lookup.");
            }
            catch { }
        }
        progress?.Report($"  Phantom proto IDs: {keptOriginal} kept original, {hijackedToVanilla} hijacked to a vanilla proto ID of the same type, {skippedInManagedProtos} kept with original mod IDs (in ManagedProtos — game handles via PhantomProto), {reassigned} assigned placeholder IDs (these will crash the game if referenced).");
    }

    /// <summary>
    /// Reassigns <paramref name="stub"/>'s Proto.Id to the ID of any vanilla proto whose
    /// runtime type IS-A the stub's runtime type (or a known intermediate base type).
    /// Returns true if a vanilla candidate was found and the ID was reassigned.
    /// <para/>
    /// Critical correctness rule: the candidate's concrete type MUST be assignable to the
    /// stub's type's enclosing SlimIdManager-bucket. Picking a candidate of a sibling or
    /// more-base type would pass the validator (the stub still gets re-serialised under
    /// its original class name) but crash the game's typed deserializer, e.g.
    /// <c>Failed cast loaded proto 'MiningDesignator (TerrainDesignationProto)' to 'ProductProto'</c>.
    /// <para/>
    /// Lookup order:
    /// <list type="number">
    ///   <item>Exact concrete type — preserves identity perfectly.</item>
    ///   <item>Any vanilla subclass of the stub's exact type (via <c>ByAssignableType</c>).</item>
    ///   <item>Walk stub's base chain (BaseType → BaseType → …) but STOP before <c>Proto</c>.
    ///   For each intermediate base, look up subclasses via <c>ByAssignableType</c>. This
    ///   handles mod-defined subclasses like <c>COIExtended.ModProductProto : ProductProto</c>
    ///   by finding any vanilla <c>ProductProto</c> as a substitute.</item>
    /// </list>
    /// We never look up <c>ByAssignableType[Proto]</c> because that would match unrelated
    /// proto kinds and break SlimIdManager typed casts.
    /// </summary>
    private bool TryHijackToVanillaIdOfSameType(
        object stub,
        FieldInfo fiProtoId,
        ConstructorInfo ctorProtoID,
        HashSet<string> seenIds)
    {
        if (_protoHealingLookup is null) return false;
        var stubType = stub.GetType();

        // Tier A: exact concrete type match.
        if (_protoHealingLookup.ByExactType.TryGetValue(stubType, out var exactCandidates)
            && TryAssignFromCandidates(stub, fiProtoId, ctorProtoID, seenIds, exactCandidates))
        {
            return true;
        }

        // Tier B: vanilla subclasses of stubType (handles stub being an unsubclassed vanilla
        // base like ProductProto where ByExactType key happens to be missing because every
        // vanilla product is a more-specific subclass).
        if (_protoHealingLookup.ByAssignableType.TryGetValue(stubType, out var subOfStub)
            && TryAssignFromCandidates(stub, fiProtoId, ctorProtoID, seenIds, subOfStub))
        {
            return true;
        }

        // Tier C: walk stub's base chain (excluding Proto itself) and look for any vanilla
        // proto that is a subclass of one of those intermediate bases. This is what handles
        // mod-only stub classes whose base chain reaches a vanilla SlimIdManager bucket.
        for (var t = stubType.BaseType; t is not null && t != typeof(object); t = t.BaseType)
        {
            // Indexing stops before Proto in BuildProtoHealingLookup, so this dictionary
            // never contains Proto as a key. The defensive check below is belt-and-braces.
            if (t.FullName == "Mafi.Core.Prototypes.Proto") break;
            if (_protoHealingLookup.ByAssignableType.TryGetValue(t, out var subOfBase)
                && TryAssignFromCandidates(stub, fiProtoId, ctorProtoID, seenIds, subOfBase))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryAssignFromCandidates(
        object stub,
        FieldInfo fiProtoId,
        ConstructorInfo ctorProtoID,
        HashSet<string> seenIds,
        List<object> candidates)
    {
        if (_protoHealingLookup is null || candidates is null || candidates.Count == 0) return false;

        // First pass: prefer a candidate ID that hasn't been claimed yet (one stub per
        // vanilla proto). Keeps slim-ID slots distinct when possible.
        foreach (var cand in candidates)
        {
            string? candId = _protoHealingLookup.GetProtoIdString(cand);
            if (string.IsNullOrEmpty(candId)) continue;
            if (!seenIds.Add(candId)) continue;
            try
            {
                var newId = ctorProtoID.Invoke(new object[] { candId })!;
                fiProtoId.SetValue(stub, newId);
                return true;
            }
            catch
            {
                seenIds.Remove(candId);
            }
        }

        // Fallback: every candidate ID has already been claimed. Reuse one anyway —
        // the SlimIdManager will allow duplicate Proto entries pointing to the same
        // vanilla proto. Slightly wrong logically (multiple slim-IDs map to the same
        // proto) but won't crash the game on load.
        foreach (var cand in candidates)
        {
            string? candId = _protoHealingLookup.GetProtoIdString(cand);
            if (string.IsNullOrEmpty(candId)) continue;
            try
            {
                var newId = ctorProtoID.Invoke(new object[] { candId })!;
                fiProtoId.SetValue(stub, newId);
                return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>
    /// Assemblies whose contents the game ships and will resolve itself on load.
    /// Vanilla entities/protos from these assemblies must be preserved by deep edit
    /// even if their proto failed to materialize in our reflection-built ProtosDb.
    /// </summary>
    internal static readonly string[] GameAssemblyPrefixes =
        new[] { "Mafi.Core", "Mafi.Base", "Mafi.TrainsDlc", "Mafi" };

    /// <summary>
    /// Returns true if the type comes from a game/DLC assembly (not a community mod).
    /// </summary>
    internal static bool IsGameAssemblyType(Type type, string[] gameAssemblyPrefixes)
    {
        string asmName = type.Assembly.GetName().Name ?? "";
        foreach (var prefix in gameAssemblyPrefixes)
        {
            if (asmName.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || asmName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Convenience: returns true iff the type belongs to a vanilla game/DLC assembly
    /// AND is not in the removed-mod set. Vanilla items must never be stripped.
    /// </summary>
    internal static bool IsVanillaType(Type? type, HashSet<string> stripAssemblies)
    {
        if (type is null) return false;
        if (ShouldStrip(type, stripAssemblies)) return false;
        return IsGameAssemblyType(type, GameAssemblyPrefixes);
    }

    // ── Machine recipe healing ────────────────────────────────────────────

    /// <summary>
    /// After stripping phantom recipe stubs from Machine.m_recipesAssigned, machines that ran
    /// only COIExtended-exclusive recipes (HighPressureBoiler "SuperSteam", SmokeStack COI-E
    /// disposal recipe, etc.) are left with an empty m_recipesAssigned and no LastRecipeInProgress.
    /// This pass finds those machines and assigns the vanilla recipes from their (now-healed)
    /// proto.m_recipes so the machine is operable after load.
    ///
    /// For machines with exactly one vanilla recipe, LastRecipeInProgress is also set so the
    /// machine restarts automatically (e.g. BoilerGas → one gas-burn recipe → resumes running).
    /// For machines with multiple vanilla recipes (SmokeStack: 8 disposal recipes,
    /// DistillationTower: several distillation recipes) only m_recipesAssigned is filled;
    /// the player must select a recipe manually in-game.
    /// </summary>
    private void HealMachineRecipes(object resolver, IProgress<string>? progress)
    {
        var tRecipeProto = AssemblyLoader.FindType("Mafi.Core.Factory.Recipes.RecipeProto");
        if (tRecipeProto is null)
        {
            progress?.Report("  Recipe healing: RecipeProto type not found — skipped.");
            return;
        }

        // Locate EntitiesManager → m_entitiesLinear (same pattern as StripNullProtoEntitiesFromManagers).
        var fiResolved = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
        var resolved = fiResolved?.GetValue(resolver) as System.Collections.IEnumerable;
        if (resolved is null) { progress?.Report("  Recipe healing: m_resolvedObjects not found — skipped."); return; }

        var tEntitiesManager = AssemblyLoader.FindType("Mafi.Core.Entities.EntitiesManager");
        if (tEntitiesManager is null) { progress?.Report("  Recipe healing: EntitiesManager type not found — skipped."); return; }

        object? entitiesManager = null;
        foreach (var obj in resolved)
        {
            if (obj is not null && tEntitiesManager.IsAssignableFrom(obj.GetType()))
            { entitiesManager = obj; break; }
        }
        if (entitiesManager is null) { progress?.Report("  Recipe healing: EntitiesManager not found in resolver — skipped."); return; }

        var fiLinear = FindFieldDeep(entitiesManager.GetType(), "m_entitiesLinear");
        var entitiesLinear = fiLinear?.GetValue(entitiesManager);
        if (entitiesLinear is null) { progress?.Report("  Recipe healing: m_entitiesLinear not found — skipped."); return; }

        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Per-type FieldInfo cache so we do GetField only once per concrete type.
        var fiRecipesAssignedCache = new Dictionary<Type, FieldInfo?>();
        var fiLastRecipeCache       = new Dictionary<Type, FieldInfo?>();
        var fiProtoCache            = new Dictionary<Type, FieldInfo?>();
        var fiMachineRecipesCache   = new Dictionary<Type, FieldInfo?>();

        int healed = 0, alsoRestored = 0, skipped = 0;

        foreach (var entity in (System.Collections.IEnumerable)entitiesLinear)
        {
            if (entity is null) continue;
            var entityType = entity.GetType();

            // Check for m_recipesAssigned field (only Machine-family entities have it).
            if (!fiRecipesAssignedCache.TryGetValue(entityType, out var fiRA))
                fiRecipesAssignedCache[entityType] = fiRA = FindFieldDeep(entityType, "m_recipesAssigned");
            if (fiRA is null) continue;

            // Only process if list is non-null and empty (phantom recipes were stripped).
            object? recipesAssigned;
            try { recipesAssigned = fiRA.GetValue(entity); } catch { continue; }
            if (recipesAssigned is null) continue;
            var countProp = recipesAssigned.GetType().GetProperty("Count", allFlags);
            int assignedCount = countProp?.GetValue(recipesAssigned) is int ac ? ac : -1;
            if (assignedCount != 0) continue;

            // Read the machine's healed proto.
            if (!fiProtoCache.TryGetValue(entityType, out var fiProto))
                fiProtoCache[entityType] = fiProto = FindFieldDeep(entityType, "m_proto");
            if (fiProto is null) { skipped++; continue; }

            object? proto;
            try { proto = fiProto.GetValue(entity); } catch { skipped++; continue; }
            if (proto is null) { skipped++; continue; }
            var protoType = proto.GetType();

            // Read vanilla proto's m_recipes (Lyst<RecipeProto>).
            if (!fiMachineRecipesCache.TryGetValue(protoType, out var fiMR))
                fiMachineRecipesCache[protoType] = fiMR = FindFieldDeep(protoType, "m_recipes");
            if (fiMR is null) { skipped++; continue; }

            object? protoRecipes;
            try { protoRecipes = fiMR.GetValue(proto); } catch { skipped++; continue; }
            if (protoRecipes is null) { skipped++; continue; }

            // Collect the actual recipe objects.
            var recipeList = new List<object>();
            try
            {
                foreach (var r in (System.Collections.IEnumerable)protoRecipes)
                    if (r is not null && tRecipeProto.IsAssignableFrom(r.GetType()))
                        recipeList.Add(r);
            }
            catch { skipped++; continue; }

            if (recipeList.Count == 0) { skipped++; continue; }

            // Find Lyst<RecipeProto>.Add(RecipeProto) — the non-generic Add taking a ref type.
            var addMethod = recipesAssigned.GetType().GetMethods(allFlags)
                .FirstOrDefault(m => m.Name == "Add"
                    && m.GetParameters().Length == 1
                    && !m.IsGenericMethodDefinition
                    && !m.GetParameters()[0].ParameterType.IsValueType);
            if (addMethod is null) { skipped++; continue; }

            bool anyAdded = false;
            foreach (var recipe in recipeList)
            {
                try { addMethod.Invoke(recipesAssigned, new[] { recipe }); anyAdded = true; }
                catch { }
            }
            if (!anyAdded) { skipped++; continue; }

            healed++;
            if (healed <= 30)
                progress?.Report($"    Recipe heal: {entityType.Name}.m_recipesAssigned ← {recipeList.Count} recipe(s) from {protoType.Name}");

            // For machines with exactly one vanilla recipe, also restore LastRecipeInProgress
            // so the machine auto-resumes (e.g. BoilerGas has 1 recipe → starts immediately).
            if (recipeList.Count != 1) continue;

            if (!fiLastRecipeCache.TryGetValue(entityType, out var fiLR))
                fiLastRecipeCache[entityType] = fiLR = FindFieldDeep(entityType, "LastRecipeInProgress");
            if (fiLR is null) continue;

            try
            {
                // LastRecipeInProgress is Option<RecipeProto> — create Some(recipe).
                var someVal = MakeOptionSome(tRecipeProto, recipeList[0]);
                fiLR.SetValue(entity, someVal);
                alsoRestored++;
                if (alsoRestored <= 10)
                    progress?.Report($"      → also restored LastRecipeInProgress for single-recipe {entityType.Name}");
            }
            catch { }
        }

        progress?.Report($"  Recipe healing: {healed} machines assigned vanilla recipes" +
            (alsoRestored > 0 ? $" ({alsoRestored} also had LastRecipeInProgress restored)" : "") +
            $", {skipped} skipped (no applicable proto recipes).");
    }

    // ── Option helpers ────────────────────────────────────────────────────

    private object MakeOptionNone(Type innerType)
    {
        var optionType = AssemblyLoader.FindType("Mafi.Option`1")
            ?? AppDomain.CurrentDomain
                        .GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                        .FirstOrDefault(t => t.Name == "Option`1" && t.Namespace == "Mafi");

        if (optionType is null)
            return null!;

        var closedOption = optionType.MakeGenericType(innerType);
        var noneField = closedOption.GetField("None", BindingFlags.Public | BindingFlags.Static);
        return noneField?.GetValue(null) ?? Activator.CreateInstance(closedOption)!;
    }

    private object MakeOptionSome(Type innerType, object value)
    {
        var optionType = AssemblyLoader.FindType("Mafi.Option`1")
            ?? AppDomain.CurrentDomain
                        .GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                        .FirstOrDefault(t => t.Name == "Option`1" && t.Namespace == "Mafi");

        if (optionType is null)
            return null!;

        var closedOption = optionType.MakeGenericType(innerType);

        var someMethod = closedOption.GetMethod("Some", BindingFlags.Public | BindingFlags.Static);
        if (someMethod is not null)
            return someMethod.Invoke(null, new[] { value })!;

        var optionNonGeneric = AssemblyLoader.FindType("Mafi.Option");
        if (optionNonGeneric is not null)
        {
            var miSome = optionNonGeneric.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Some" && m.IsGenericMethodDefinition);
            if (miSome is not null)
                return miSome.MakeGenericMethod(innerType).Invoke(null, new[] { value })!;
        }

        var ctor = closedOption.GetConstructor(new[] { innerType });
        if (ctor is not null)
            return ctor.Invoke(new[] { value });

        var inst = Activator.CreateInstance(closedOption)!;
        var valueProp = closedOption.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (valueProp?.CanWrite == true)
        {
            valueProp.SetValue(inst, value);
            return inst;
        }
        var valueField = closedOption.GetField("m_value", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? closedOption.GetField("Value", BindingFlags.NonPublic | BindingFlags.Instance);
        if (valueField is not null)
        {
            valueField.SetValue(inst, value);
            return inst;
        }
        return inst;
    }
}
