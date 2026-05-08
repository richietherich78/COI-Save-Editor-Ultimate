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
    /// NullifyPhantomProtoIds (which replaces them with the game's phantom product ID).
    /// <para/>
    /// Why we cannot hijack these stubs to a random vanilla product: if we write "IronOre" at the
    /// slot where "AlnicoIngot" lived, the game resolves that slot to the real IronOre proto and
    /// consumes it from the vanilla set.  When initAfterLoad later reaches the real IronOre
    /// position it is already claimed → "Missing proto detected" → IronOre gets a brand-new
    /// appended SlimId → ProductStats[oldSlimId] mismatch → all vanilla storage stats show 0.
    /// <para/>
    /// Why we cannot keep the original mod ID: the game's ReadWeakProtoRef returns <c>null</c>
    /// (not a PhantomProto) for IDs not in its ProtosDb.  A null entry in ManagedProtos causes
    /// ProductsManager.onNewDay() → UnlockedProtosDb.Unlock(null) → NullReferenceException every
    /// in-game day.
    /// <para/>
    /// Correct fix (applied in NullifyPhantomProtoIds): replace these stubs with the game's own
    /// <c>__PHANTOM__PRODUCT__</c> ID.  The game resolves it to a properly-initialized ProductProto
    /// and its initAfterLoad treats phantom-product slots as skippable holes, so every vanilla
    /// product stays at its correct SlimId position and ProductStats are preserved.
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
        var stubsInManagedProtos = _stubsInSlimIdManagers ?? new HashSet<object>(ReferenceEqualityComparer.Instance);
        progress?.Report($"  SlimIdManager stubs to replace with phantom product ID: {stubsInManagedProtos.Count}.");

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

        // ProductProto base type — only product stubs may be replaced with __PHANTOM__PRODUCT__.
        // Non-product SlimIdManager stubs (TerrainMaterialProto, TileSurfaceProto, etc.) must NOT
        // receive a ProductProto ID: the game's typed deserialization would immediately throw
        // CorruptedSaveException ("Failed cast … to TerrainMaterialProto").
        var tProductProto = AssemblyLoader.FindType("Mafi.Core.Products.ProductProto");

        // Assemblies whose protos the game ships — the game will resolve these IDs itself.
        var gameAssemblyPrefixes = GameAssemblyPrefixes;

        // Canonical phantom product IDs registered in the vanilla v0.8.3+ ProtosDb.
        // Stubs in ManagedProtos (mod products that no longer exist) are replaced with
        // these IDs so the game resolves them to a properly-initialized ProductProto instead
        // of returning null.  A null entry at any ManagedProtos slot causes
        // ProductsManager.onNewDay() → UnlockedProtosDb.Unlock(null) → NullReferenceException
        // every in-game day.  Using the game's own phantom product as the replacement is
        // safe: SlimIdManager.initAfterLoad treats phantom-product slots as skippable holes,
        // so vanilla products stay at their correct SlimId positions.
        //
        // IMPORTANT: these phantom product IDs are registered by SlimIdManager.initAfterLoad
        // at game runtime, NOT by GameBuilder.RegisterModsPrototypes, so they will typically
        // NOT appear in our editor's ProtosDb.  We write them unconditionally anyway: the game
        // can always resolve them from its own ProtosDb on load.  The ById checks below are
        // only used to pick the most specific variant (FLUID/LOOSE/COUNTABLE); falling back to
        // the base phantom ID is always correct.
        const string PhantomProductId   = "__PHANTOM__PRODUCT__";
        const string PhantomCountableId = "__PHANTOM__PRODUCT__COUNTABLE__";
        const string PhantomFluidId     = "__PHANTOM__PRODUCT__FLUID__";
        const string PhantomLooseId     = "__PHANTOM__PRODUCT__LOOSE__";
        // Always true: the game guarantees these IDs exist in vanilla v0.8.3+.
        // The ById checks are belt-and-braces; we use their results only for variant selection.
        bool hasPhantomProduct   = true;
        bool hasPhantomCountable = _protoHealingLookup?.ById.ContainsKey(PhantomCountableId) == true;
        bool hasPhantomFluid     = _protoHealingLookup?.ById.ContainsKey(PhantomFluidId)     == true;
        bool hasPhantomLoose     = _protoHealingLookup?.ById.ContainsKey(PhantomLooseId)     == true;
        if (_protoHealingLookup?.ById.ContainsKey(PhantomProductId) != true)
            progress?.Report($"  Note: {PhantomProductId} not in editor ProtosDb (registered by game at runtime) — will write ID unconditionally.");

        // Track IDs we've already seen to ensure uniqueness (game's SlimIdManager requires it).
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        int keptOriginal = 0;
        int reassigned = 0;
        int hijackedToVanilla = 0;
        int phantomIndex = 0;

        int replacedWithPhantomProduct = 0;
        foreach (var stub in _phantomProtoStubs)
        {
            try
            {
                // Stubs in SlimIdManager.ManagedProtos represent removed mod products/materials/etc.
                // For PRODUCT stubs we cannot hijack them to a random vanilla product (that would steal
                // the vanilla product's SlimId, corrupting storage stats), but we also cannot keep the
                // original mod ID: the game's ReadWeakProtoRef returns null for unknown IDs, leaving
                // ManagedProtos[N] = null, which causes ProductsManager.onNewDay() →
                // UnlockedProtosDb.Unlock(null) → NullRef every in-game day.
                // Solution: write the game's own phantom-product ID.  The game resolves it to a
                // properly-initialized ProductProto and its initAfterLoad treats phantom-product entries
                // as skippable holes, preserving vanilla SlimIds.
                //
                // For NON-PRODUCT stubs (TerrainMaterialProto, TileSurfaceProto, etc.) we must NOT use
                // __PHANTOM__PRODUCT__ — the game would try to cast ProductProto → TerrainMaterialProto
                // and immediately throw CorruptedSaveException.  These fall through to the normal
                // hijack/placeholder logic below.
                if (stubsInManagedProtos.Contains(stub))
                {
                    bool isProductStub = tProductProto is not null
                        && tProductProto.IsAssignableFrom(stub.GetType());

                    if (isProductStub && hasPhantomProduct)
                    {
                        string replacementId = PickPhantomProductId(stub.GetType().Name,
                            hasPhantomCountable, hasPhantomFluid, hasPhantomLoose,
                            PhantomProductId, PhantomCountableId, PhantomFluidId, PhantomLooseId);
                        fiProtoId.SetValue(stub, ctorProtoID.Invoke(new object[] { replacementId })!);
                        replacedWithPhantomProduct++;
                        continue;
                    }

                    // Non-product SlimIdManager stub — fall through to hijack/placeholder logic.
                }

                string? originalId = null;
                var idObj = fiProtoId.GetValue(stub);
                if (idObj is not null)
                {
                    // Proto.ID has a ToString() or Value property that gives the string ID.
                    var valueProp = idObj.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    originalId = (valueProp?.GetValue(idObj) as string) ?? idObj.ToString();
                }

                // Sibling stubs (e.g. ProductStats[N].Product) share the same original mod ID
                // as the ManagedProtos stub but are DIFFERENT objects (the factory creates one
                // stub per deserialization call).  Replace them with the same phantom product ID
                // so both the ManagedProtos slot and any parallel arrays (ProductStats, etc.)
                // resolve to the same properly-initialized phantom product on load.
                // Guard: only apply this to product stubs — sibling stubs for non-product types
                // (e.g. a TerrainMaterialProto referenced by its ID in another array) must not
                // receive a ProductProto ID.
                if (_slimIdProtectedProtoIdStrings is not null
                    && !string.IsNullOrEmpty(originalId)
                    && _slimIdProtectedProtoIdStrings.Contains(originalId!))
                {
                    bool isSiblingProductStub = tProductProto is not null
                        && tProductProto.IsAssignableFrom(stub.GetType());

                    if (isSiblingProductStub && hasPhantomProduct)
                    {
                        string replacementId = PickPhantomProductId(stub.GetType().Name,
                            hasPhantomCountable, hasPhantomFluid, hasPhantomLoose,
                            PhantomProductId, PhantomCountableId, PhantomFluidId, PhantomLooseId);
                        fiProtoId.SetValue(stub, ctorProtoID.Invoke(new object[] { replacementId })!);
                        replacedWithPhantomProduct++;
                        continue;
                    }

                    // Non-product sibling stub — fall through to hijack/placeholder logic.
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
        progress?.Report($"  Phantom proto IDs: {keptOriginal} kept original, {hijackedToVanilla} hijacked to a vanilla proto ID of the same type, {replacedWithPhantomProduct} replaced with {PhantomProductId} (mod product slots in SlimIdManagers — avoids onNewDay NullRef), {reassigned} assigned placeholder IDs (these will crash the game if referenced).");
    }

    private static string PickPhantomProductId(
        string stubTypeName,
        bool hasCountable, bool hasFluid, bool hasLoose,
        string baseId, string countableId, string fluidId, string looseId)
    {
        // These phantom IDs are always registered in vanilla v0.8.3+ at game runtime —
        // do NOT gate on hasFluid/hasLoose/hasCountable (those are false because the editor's
        // ProtosDb is built before SlimIdManager.initAfterLoad registers them).  Selecting the
        // wrong base ID causes CorruptedSaveException: "Failed cast ProductProto to LooseProductProto".
        if (stubTypeName.Contains("Fluid",      StringComparison.OrdinalIgnoreCase)) return fluidId;
        if (stubTypeName.Contains("Loose",      StringComparison.OrdinalIgnoreCase)) return looseId;
        if (stubTypeName.Contains("Countable",  StringComparison.OrdinalIgnoreCase)) return countableId;
        return baseId;
    }

    /// <summary>
    /// Sets null <c>ProductStats[i].Product</c> fields to the appropriate vanilla phantom product
    /// proto so <c>ProductsManager.onNewDay()</c> never calls <c>UnlockedProtosDb.Unlock(null)</c>.
    /// <para/>
    /// Root cause: <c>ProductStats[i].Product</c> is a direct (non-weak) <c>ProductProto</c> field
    /// that BlobReader fills via a ProtosDb look-up.  Unknown mod product IDs return null — no
    /// phantom stub is created, so <c>NullifyPhantomProtoIds</c> never sees them.  The null
    /// propagates into the output save, and <c>onNewDay</c> crashes every in-game day.
    /// <para/>
    /// Fix: after <c>NullifyPhantomProtoIds</c> modifies the parallel <c>ManagedProtos</c> stubs,
    /// we walk <c>ProductsManager.ProductStats</c> in lock-step with <c>ManagedProtos</c> and
    /// replace each null Product with the phantom product proto that matches the stub's original
    /// type (Fluid → <c>__PHANTOM__PRODUCT__FLUID__</c>, Countable → <c>__PHANTOM__PRODUCT__COUNTABLE__</c>,
    /// else <c>__PHANTOM__PRODUCT__</c>).
    /// </summary>
    private void FixProductStatsNullProducts(object resolver, IProgress<string>? progress)
    {
        if (_protoHealingLookup is null) return;

        const string PhantomProductId   = "__PHANTOM__PRODUCT__";
        const string PhantomCountableId = "__PHANTOM__PRODUCT__COUNTABLE__";
        const string PhantomFluidId     = "__PHANTOM__PRODUCT__FLUID__";
        const string PhantomLooseId     = "__PHANTOM__PRODUCT__LOOSE__";
        _protoHealingLookup.ById.TryGetValue(PhantomProductId,   out var phantomBase);
        _protoHealingLookup.ById.TryGetValue(PhantomCountableId, out var phantomCountable);
        _protoHealingLookup.ById.TryGetValue(PhantomFluidId,     out var phantomFluid);
        _protoHealingLookup.ById.TryGetValue(PhantomLooseId,     out var phantomLoose);
        var fallback = phantomCountable ?? phantomBase;

        // The game's own __PHANTOM__PRODUCT__ protos are registered by SlimIdManager.initAfterLoad
        // at game runtime, NOT by GameBuilder.RegisterModsPrototypes — so they are absent from our
        // editor's ProtosDb and the above TryGetValue calls all return null.
        // Fall back to any vanilla ProductProto from the healing lookup: the game only calls
        // UnlockedProtosDb.Unlock(Product) to mark the slot, and the real proto is resolved from
        // ManagedProtos by SlimId on load — so any non-null ProductProto placeholder prevents
        // the onNewDay NullReferenceException.
        if (fallback is null)
        {
            var tProductProto = AssemblyLoader.FindType("Mafi.Core.Products.ProductProto");
            if (tProductProto is not null)
            {
                if (_protoHealingLookup.ByAssignableType.TryGetValue(tProductProto, out var productCandidates)
                    && productCandidates.Count > 0)
                    fallback = productCandidates[0];
                else if (_protoHealingLookup.ByExactType.TryGetValue(tProductProto, out var exactProducts)
                    && exactProducts.Count > 0)
                    fallback = exactProducts[0];
            }
        }

        if (fallback is null)
        {
            progress?.Report("  FixProductStatsNullProducts: no ProductProto found in vanilla ProtosDb, skipping.");
            return;
        }
        progress?.Report($"  FixProductStatsNullProducts: using '{_protoHealingLookup.GetProtoIdString(fallback) ?? fallback.GetType().Name}' as null-Product placeholder.");

        // _stubsInSlimIdManagers may be null/empty (e.g. when CollectSlimIdManagerStubs found
        // no stubs in ManagedProtos), but ProductStats.Product can still be null for any mod
        // product slot that was written as a weak proto ref returning null.  Always scan; the
        // SlimIdManager stub set is optional metadata used only to pick the right phantom variant.
        var stubsInManaged = _stubsInSlimIdManagers ?? new HashSet<object>(ReferenceEqualityComparer.Instance);
        progress?.Report($"  FixProductStatsNullProducts: scanning (SlimIdManager stubs known={stubsInManaged.Count})…");

        int totalFixed = 0;
        foreach (var obj in CollectAllResolverObjects(resolver))
        {
            if (obj is null) continue;
            if (!obj.GetType().Name.Contains("ProductsManager")) continue;

            try
            {
                var tPM     = obj.GetType();
                var fiStats = FindFieldDeep(tPM, "<ProductStats>k__BackingField")
                           ?? FindFieldDeep(tPM, "ProductStats");
                if (fiStats is null) continue;

                var statsBoxed = fiStats.GetValue(obj);
                if (statsBoxed is null) continue;

                // ManagedProtos is optional — used for per-slot phantom variant selection.
                List<object?>? managedList = null;
                var fiSlim = FindFieldDeep(tPM, "<SlimIdManager>k__BackingField")
                          ?? FindFieldDeep(tPM, "SlimIdManager");
                if (fiSlim is not null)
                {
                    var slimMgrBoxed = fiSlim.GetValue(obj);
                    if (slimMgrBoxed is not null)
                    {
                        var fiManaged = FindFieldDeep(slimMgrBoxed.GetType(), "ManagedProtos");
                        var managedBoxed = fiManaged?.GetValue(slimMgrBoxed);
                        if (managedBoxed is not null)
                            managedList = AuditTryEnumerate(managedBoxed)?.ToList<object?>();
                    }
                }

                var statsList = AuditTryEnumerate(statsBoxed)?.ToList();
                if (statsList is null) continue;

                // Find the Product field on the ProductStats element type.
                FieldInfo? fiProduct = null;
                foreach (var ps in statsList)
                {
                    if (ps is null) continue;
                    fiProduct = ps.GetType().GetField("Product",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    break;
                }
                if (fiProduct is null) continue;

                int count = statsList.Count;
                int fixedHere = 0;
                for (int i = 0; i < count; i++)
                {
                    var ps = statsList[i];
                    if (ps is null) continue;
                    if (fiProduct.GetValue(ps) is not null) continue;

                    // Use the stub already sitting in ManagedProtos[i].
                    // NullifyPhantomProtoIds has already written the correct typed phantom ID
                    // (__PHANTOM__PRODUCT__FLUID__, __PHANTOM__PRODUCT__LOOSE__, etc.) onto it.
                    // Writing that same stub object into ProductStats[i].Product means both slots
                    // serialize the same ID → game resolves both to the same phantom proto on load
                    // → initAfterLoad comparison matches → no "Products changed" stat wipe.
                    // Fall back to the vanilla placeholder only if ManagedProtos alignment is absent.
                    object? phantom = fallback;
                    if (managedList is not null && i < managedList.Count)
                    {
                        var managedStub = managedList[i];
                        if (managedStub is not null)
                            phantom = managedStub;
                    }

                    fiProduct.SetValue(ps, phantom);
                    fixedHere++;
                }

                if (fixedHere > 0)
                {
                    totalFixed += fixedHere;
                    progress?.Report($"  Fixed {fixedHere} null Product refs in {obj.GetType().Name}.ProductStats.");
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"  FixProductStatsNullProducts error on {obj?.GetType().Name ?? "?"}: {ex.Message}");
            }
        }

        progress?.Report(totalFixed == 0
            ? "  FixProductStatsNullProducts: no null ProductStats.Product refs found."
            : $"  FixProductStatsNullProducts: {totalFixed} null Product refs patched with phantom product protos (prevents onNewDay NullRef).");
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

    // ── Phantom product cargo clearing ────────────────────────────────────

    /// <summary>
    /// Clears phantom product cargo from Transport entities so that the game's renderer
    /// never tries to load a mesh for a phantom product (which has no visual asset).
    /// <para/>
    /// Root cause: mod product slots in <c>SlimIdManager.ManagedProtos</c> are replaced
    /// with <c>__PHANTOM__PRODUCT__*</c> IDs by <see cref="NullifyPhantomProtoIds"/>.
    /// Transport entities store their in-transit cargo as <c>ProductSlimId</c> values
    /// (integer indices into <c>ManagedProtos</c>). On game load, those indices resolve
    /// to phantom product protos, which have no mesh asset — generating
    /// "Trying to render product #N that has no mesh" for every render frame.
    /// <para/>
    /// Fix: find every transport entity whose <c>m_products</c> queue contains an item
    /// whose <c>SlimId.Value</c> maps to a phantom slot, then clear the entire queue.
    /// Items on the same belt that carried vanilla products are also lost, but this is
    /// acceptable — belts are refilled quickly and keeping phantom cargo would cause
    /// continuous mesh-load errors while the game runs.
    /// </summary>
    private void ClearPhantomProductCargoFromTransports(object resolver, IProgress<string>? progress)
    {
        if (_stubsInSlimIdManagers is null || _stubsInSlimIdManagers.Count == 0) return;

        const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Build a set of phantom slim ID values (ushort indices into ManagedProtos).
        var phantomSlimIdValues = new HashSet<ushort>();
        var slimManagers = new List<object>();

        var allObjects = CollectAllResolverObjects(resolver);
        foreach (var obj in allObjects)
        {
            if (obj is null) continue;
            if (obj.GetType().Name.Contains("SlimIdManager"))
            {
                slimManagers.Add(obj);
                continue;
            }
            for (var t = obj.GetType(); t is not null && t != typeof(object); t = t.BaseType)
            {
                foreach (var fi in t.GetFields(allInst | BindingFlags.DeclaredOnly))
                {
                    if (!fi.FieldType.Name.Contains("SlimIdManager")) continue;
                    try { var val = fi.GetValue(obj); if (val is not null) slimManagers.Add(val); } catch { }
                }
            }
        }

        foreach (var slimMgr in slimManagers)
        {
            var fiManaged = FindFieldDeep(slimMgr.GetType(), "ManagedProtos");
            if (fiManaged is null) continue;
            try
            {
                var boxedArr = fiManaged.GetValue(slimMgr);
                if (boxedArr is null) continue;
                var items = AuditTryEnumerate(boxedArr)?.ToList();
                if (items is null) continue;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] is not null && _stubsInSlimIdManagers.Contains(items[i]!))
                        phantomSlimIdValues.Add((ushort)i);
                }
            }
            catch { }
        }

        if (phantomSlimIdValues.Count == 0)
        {
            progress?.Report("  ClearPhantomProductCargo: no phantom slim IDs found, nothing to clear.");
            return;
        }
        progress?.Report($"  ClearPhantomProductCargo: {phantomSlimIdValues.Count} phantom slim ID slot(s) identified.");

        // Find EntitiesManager.
        var tEM = AssemblyLoader.FindType("Mafi.Core.Entities.EntitiesManager");
        if (tEM is null) { progress?.Report("  ClearPhantomProductCargo: EntitiesManager type not found."); return; }

        object? em = null;
        foreach (var obj in CollectAllResolverObjects(resolver))
        {
            if (obj is not null && tEM.IsAssignableFrom(obj.GetType())) { em = obj; break; }
        }
        if (em is null) { progress?.Report("  ClearPhantomProductCargo: EntitiesManager not found in resolver."); return; }

        var fiLinear = FindFieldDeep(em.GetType(), "m_entitiesLinear");
        var entities = fiLinear?.GetValue(em);
        if (entities is null) return;

        var fiProductsCache = new Dictionary<Type, FieldInfo?>();
        FieldInfo? fiSlimId = null;
        FieldInfo? fiSlimIdValue = null;
        int checked_ = 0, cleared = 0;

        foreach (var entity in (System.Collections.IEnumerable)entities)
        {
            if (entity is null) continue;
            var entityType = entity.GetType();

            if (!fiProductsCache.TryGetValue(entityType, out var fiQ))
                fiProductsCache[entityType] = fiQ = FindFieldDeep(entityType, "m_products");
            if (fiQ is null) continue;

            object? queue;
            try { queue = fiQ.GetValue(entity); } catch { continue; }
            if (queue is null) continue;

            var countProp = queue.GetType().GetProperty("Count", allInst);
            int count = countProp?.GetValue(queue) is int c ? c : 0;
            if (count == 0) continue;

            checked_++;
            bool hasPhantom = false;
            try
            {
                foreach (var itemObj in (System.Collections.IEnumerable)queue)
                {
                    if (itemObj is null) continue;
                    if (fiSlimId is null)
                        fiSlimId = itemObj.GetType().GetField("SlimId", allInst);
                    if (fiSlimId is null) break;

                    var slimIdBoxed = fiSlimId.GetValue(itemObj);
                    if (slimIdBoxed is null) continue;
                    if (fiSlimIdValue is null)
                        fiSlimIdValue = slimIdBoxed.GetType().GetField("Value", allInst);
                    if (fiSlimIdValue is null) break;

                    var rawValue = fiSlimIdValue.GetValue(slimIdBoxed);
                    ushort slimVal = rawValue switch
                    {
                        ushort us => us,
                        int iv    => (ushort)iv,
                        _         => 0
                    };
                    if (phantomSlimIdValues.Contains(slimVal)) { hasPhantom = true; break; }
                }
            }
            catch { continue; }

            if (!hasPhantom) continue;

            try
            {
                var miClear = queue.GetType().GetMethod("Clear", Type.EmptyTypes);
                miClear?.Invoke(queue, null);
                cleared++;
                if (cleared <= 25)
                    progress?.Report($"    Cleared phantom cargo on {entityType.Name}");
            }
            catch { }
        }

        progress?.Report($"  ClearPhantomProductCargo: checked {checked_} transport(s) with items, cleared {cleared} queue(s) of phantom cargo (prevents per-frame mesh errors).");
    }

    /// <summary>
    /// Removes phantom-proto food entries from every Settlement's food-data structures.
    /// <para/>
    /// COI-Extended food protos (FoodCanFish, FoodCanFruit, FoodCanVegetables, etc.) are stored
    /// in two places inside each Settlement:
    /// <list type="bullet">
    ///   <item><c>m_foodTypesMap</c> — <c>Dict&lt;ProductProto, FoodData&gt;</c> keyed by
    ///         <c>FoodProto.Product</c> (the associated <c>CountableProductProto</c>).</item>
    ///   <item><c>m_foodCategories[i].FoodTypes</c> — <c>ImmutableArray&lt;FoodData&gt;</c>
    ///         where each element's <c>FoodData.Prototype</c> is the <c>FoodProto</c>.</item>
    /// </list>
    /// When any of those prototypes is a phantom stub (mod removed), <c>Settlement.OnNewDay</c>
    /// calls <c>GetMaxUnityProvidedFor(phantomFoodProto)</c> → exception → "Too many exceptions
    /// by 'onNewDay'" → settlement unity and food bonuses collapse to <c>0/0</c>.
    /// <para/>
    /// This pass removes all phantom-keyed entries so only real food protos remain.
    /// </summary>
    private void FixSettlementPhantomFoodData(object resolver, IProgress<string>? progress)
    {
        if (_phantomProtoStubs is null || _phantomProtoStubs.Count == 0)
        {
            progress?.Report("  FixSettlementPhantomFoodData: no phantom stubs recorded — skipping.");
            return;
        }

        var tSettlement = AssemblyLoader.FindType("Mafi.Core.Buildings.Settlements.Settlement");
        if (tSettlement is null)
        {
            progress?.Report("  FixSettlementPhantomFoodData: Settlement type not found — skipping.");
            return;
        }

        // Settlements are NOT top-level resolver objects — they live in
        // SettlementsManager.m_settlements (a Lyst<Settlement>).  Find the manager first.
        var tSettlementsManager = AssemblyLoader.FindType("Mafi.Core.Buildings.Settlements.SettlementsManager");
        System.Collections.IEnumerable? settlementObjects = null;

        if (tSettlementsManager is not null)
        {
            foreach (var ro in CollectAllResolverObjects(resolver))
            {
                if (ro is null) continue;
                if (!tSettlementsManager.IsAssignableFrom(ro.GetType())) continue;
                var fiSettlements = FindFieldDeep(ro.GetType(), "m_settlements");
                var settlementsVal = fiSettlements?.GetValue(ro);
                if (settlementsVal is System.Collections.IEnumerable ie)
                { settlementObjects = ie; break; }
            }
        }

        // Fallback: scan all resolver objects directly (handles edge cases).
        if (settlementObjects is null)
            settlementObjects = CollectAllResolverObjects(resolver)
                .Where(o => o is not null && tSettlement.IsAssignableFrom(o.GetType()))
                .Cast<object>();

        const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ── Resolve IPropertiesDb so we can re-inject m_unityProductionMultiplier ─────
        // COI-Extended may have serialized its own IProperty<Percent> implementation into
        // the save. When it's stripped, ReadGenericAs<IProperty<Percent>>() returns null
        // for saves >= v140 (the game only re-resolves it in initSelf for saves < 140).
        // Null m_unityProductionMultiplier → GetMaxUnityProvidedFor NRE on same IL offset.
        object? reResolvedUnityMultiplier = null;
        try
        {
            var tIPropertiesDb = AssemblyLoader.FindType("Mafi.Core.PropertiesDb.IPropertiesDb");
            var tPercent       = AssemblyLoader.FindType("Mafi.Percent");
            if (tIPropertiesDb is not null && tPercent is not null)
            {
                // Find IPropertiesDb in resolver objects.
                object? propsDb = null;
                foreach (var ro in CollectAllResolverObjects(resolver))
                {
                    if (ro is null) continue;
                    if (tIPropertiesDb.IsAssignableFrom(ro.GetType())) { propsDb = ro; break; }
                }
                if (propsDb is not null)
                {
                    // PropertyIds is a nested static class inside IdsCore.
                    var propIdsType = AssemblyLoader.FindType("Mafi.Core.IdsCore+PropertyIds");
                    object? unityPropId = null;
                    if (propIdsType is not null)
                    {
                        var fi = propIdsType.GetField("UnityProductionMultiplier",
                                     BindingFlags.Public | BindingFlags.Static);
                        unityPropId = fi?.GetValue(null);
                    }
                    if (unityPropId is not null)
                    {
                        // GetProperty<Percent>(PropertyId<Percent> id)
                        var miGetPropOpen = tIPropertiesDb.GetMethod("GetProperty");
                        var miGetProp = miGetPropOpen?.MakeGenericMethod(tPercent);
                        if (miGetProp is not null)
                            reResolvedUnityMultiplier = miGetProp.Invoke(propsDb, new[] { unityPropId });
                    }
                    progress?.Report($"  Settlement: re-resolved UnityProductionMultiplier = {reResolvedUnityMultiplier?.GetType().Name ?? "NULL"}");
                }
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"  Settlement: could not re-resolve UnityProductionMultiplier: {ex.Message}");
        }

        // Field caches — resolved once against the first Settlement instance found.
        FieldInfo? fiMap = null;
        FieldInfo? fiCategories = null;
        FieldInfo? fiFoodTypes = null;     // FoodCategoryData.FoodTypes
        FieldInfo? fiFoodProto = null;     // FoodData.Prototype (FoodProto)
        FieldInfo? fiUnityMult = null;     // Settlement.m_unityProductionMultiplier

        int settlementsFixed = 0;
        int totalMapRemoved = 0;
        int totalArrayRemoved = 0;

        foreach (var obj in settlementObjects)
        {
            if (obj is null) continue;

            try
            {
                var st = obj.GetType();
                progress?.Report($"  Settlement scan: {st.Name} / {obj.GetHashCode():X}");

                // ── Resolve fields on first Settlement ────────────────────────────────
                if (fiMap is null)
                {
                    fiMap        = FindFieldDeep(st, "m_foodTypesMap");
                    fiCategories = FindFieldDeep(st, "m_foodCategories");
                    fiUnityMult  = FindFieldDeep(st, "m_unityProductionMultiplier");
                }

                // ── Heal m_unityProductionMultiplier if null ───────────────────────────
                // If COI-Extended wrote its own IProperty<Percent> impl and it deserialized
                // back as null, GetMaxUnityProvidedFor crashes at .Value on a null ref.
                if (fiUnityMult is not null && reResolvedUnityMultiplier is not null)
                {
                    var current = fiUnityMult.GetValue(obj);
                    if (current is null)
                    {
                        fiUnityMult.SetValue(obj, reResolvedUnityMultiplier);
                        progress?.Report($"  Settlement {obj.GetHashCode():X}: re-injected null m_unityProductionMultiplier.");
                        settlementsFixed++;
                    }
                }

                // ─────────────────────────────────────────────────────────────────────
                // 1. Clean m_foodTypesMap: Dict<ProductProto, FoodData>
                //    Remove any entry where:
                //      (a) the KEY (ProductProto) is a phantom stub, OR
                //      (b) the VALUE's FoodData.Prototype (FoodProto) is null or phantom.
                //
                //    IMPORTANT: Mafi Dict<K,V> uses tombstone-style deletion internally.
                //    Calling Remove() marks an entry as deleted in the in-memory structure
                //    but the custom BlobWriter serializer iterates the raw backing arrays
                //    (including tombstoned slots) — so the phantom key survives
                //    serialization unchanged.
                //
                //    The correct approach: enumerate all valid KVPs, Clear() the dict,
                //    then re-Add() only the valid entries.  Clear() resets the backing
                //    arrays entirely (no tombstones), and Add() rebuilds cleanly.
                // ─────────────────────────────────────────────────────────────────────
                int mapRemoved = 0;
                if (fiMap is not null)
                {
                    var mapVal = fiMap.GetValue(obj);
                    if (mapVal is not null)
                    {
                        var mapType = mapVal.GetType();
                        var ga = mapType.GetGenericArguments();     // [ProductProto, FoodData]

                        var miClear  = mapType.GetMethod("Clear",  Type.EmptyTypes);
                        var miAdd    = ga.Length >= 2
                                     ? mapType.GetMethod("Add", new[] { ga[0], ga[1] })
                                     : null;

                        // Enumerate as IEnumerable<KeyValuePair<K,V>> — safe for any Dict.
                        var kvpType  = typeof(System.Collections.Generic.KeyValuePair<,>)
                                         .MakeGenericType(ga);
                        var fiKvpKey = kvpType.GetProperty("Key", allInst);
                        var fiKvpVal = kvpType.GetProperty("Value", allInst);

                        var goodKVPs = new List<(object key, object val)>();
                        if (mapVal is System.Collections.IEnumerable mapEnum
                                && fiKvpKey is not null && fiKvpVal is not null)
                        {
                            foreach (var kvp in mapEnum)
                            {
                                if (kvp is null) continue;
                                var key = fiKvpKey.GetValue(kvp);
                                if (key is null) continue;

                                var val = fiKvpVal.GetValue(kvp);
                                object? proto = null;
                                if (val is not null)
                                {
                                    fiFoodProto ??= FindFieldDeep(val.GetType(), "Prototype");
                                    proto = fiFoodProto?.GetValue(val);
                                }

                                bool keyIsPhantom   = _phantomProtoStubs.Contains(key);
                                bool protoIsNull     = proto is null;
                                bool protoIsPhantom  = proto is not null && _phantomProtoStubs.Contains(proto);

                                // Log every entry so we can diagnose.
                                progress?.Report($"    FoodMap entry: key={key} keyPhantom={keyIsPhantom} | FoodProto={(proto?.ToString() ?? "NULL")} protoNull={protoIsNull} protoPhantom={protoIsPhantom}");

                                if (keyIsPhantom || protoIsNull || protoIsPhantom)
                                    mapRemoved++;
                                else
                                    goodKVPs.Add((key, val!));
                            }
                        }

                        if (mapRemoved > 0 && miClear is not null && miAdd is not null)
                        {
                            // Rebuild: clear all slots, then re-insert only valid entries.
                            miClear.Invoke(mapVal, null);
                            foreach (var (k, v) in goodKVPs)
                            {
                                try { miAdd.Invoke(mapVal, new[] { k, v }); }
                                catch { }
                            }
                            progress?.Report($"    m_foodTypesMap rebuilt: removed {mapRemoved} bad entry(ies), {goodKVPs.Count} valid.");
                        }
                        else
                        {
                            progress?.Report($"    m_foodTypesMap scan: {goodKVPs.Count} clean entries, {mapRemoved} bad.");
                        }
                    }
                }

                // ─────────────────────────────────────────────────────────────────────
                // 2. Clean m_foodCategories[i].FoodTypes: ImmutableArray<FoodData>
                //    Each FoodData.Prototype is the FoodProto. Remove entries whose
                //    FoodProto is null (mod food type stripped, ReadGenericAs returned null)
                //    or is a phantom stub.  Also drop entire categories whose own
                //    Prototype (FoodCategoryProto) is null or phantom.
                //
                //    NOTE: EnumerateMafiImmutableArray uses the IImmutableArray.Array
                //    interface / m_items backing-field path to bypass struct-enumerator
                //    boxing complications that previously caused a silent empty enumeration.
                // ─────────────────────────────────────────────────────────────────────
                int arrayRemoved = 0;
                FieldInfo? fiCatProto = null;   // FoodCategoryData.Prototype (FoodCategoryProto)
                if (fiCategories is not null)
                {
                    var categoriesVal = fiCategories.GetValue(obj);
                    if (categoriesVal is not null)
                    {
                        var categoryList = EnumerateMafiImmutableArray(categoriesVal);
                        progress?.Report($"    m_foodCategories: {categoryList.Count} category/categories enumerated.");

                        // We may also need to rebuild m_foodCategories itself if any
                        // category has a null/phantom Prototype (FoodCategoryProto).
                        bool categoriesNeedRebuild = false;
                        Type? catElemType = categoryList.Count > 0 ? categoryList[0]?.GetType() : null;

                        foreach (var cat in categoryList)
                        {
                            if (cat is null) { categoriesNeedRebuild = true; continue; }
                            var catType = cat.GetType();
                            catElemType ??= catType;

                            // Resolve FoodCategoryData.Prototype field (FoodCategoryProto).
                            fiCatProto ??= FindFieldDeep(catType, "Prototype");
                            var catProto = fiCatProto?.GetValue(cat);
                            bool catProtoNull    = catProto is null;
                            bool catProtoPhantom = catProto is not null && _phantomProtoStubs.Contains(catProto);
                            progress?.Report($"    Category: Prototype={(catProto?.ToString() ?? "NULL")} null={catProtoNull} phantom={catProtoPhantom}");

                            if (catProtoNull || catProtoPhantom)
                            {
                                categoriesNeedRebuild = true;
                                arrayRemoved++;
                                continue;   // Drop the whole category.
                            }

                            // ── Scan FoodTypes within this category ─────────────────
                            fiFoodTypes ??= FindFieldDeep(catType, "FoodTypes");
                            if (fiFoodTypes is null) continue;

                            var foodTypesVal = fiFoodTypes.GetValue(cat);
                            if (foodTypesVal is null) continue;

                            var foodTypeList = EnumerateMafiImmutableArray(foodTypesVal);
                            progress?.Report($"      FoodTypes: {foodTypeList.Count} entries for category {catProto}");

                            // Resolve FoodData.Prototype field from the first non-null element.
                            if (fiFoodProto is null)
                            {
                                foreach (var fd in foodTypeList)
                                {
                                    if (fd is null) continue;
                                    fiFoodProto = FindFieldDeep(fd.GetType(), "Prototype");
                                    break;
                                }
                            }

                            if (foodTypeList.Count == 0 || fiFoodProto is null) continue;

                            var elemType = foodTypesVal.GetType().GetGenericArguments().FirstOrDefault()
                                        ?? (foodTypeList.Count > 0 ? foodTypeList[0]?.GetType() : null);
                            if (elemType is null) continue;

                            var filtered = foodTypeList
                                        .Where(fd =>
                                        {
                                            if (fd is null) return false;
                                            var proto = fiFoodProto!.GetValue(fd);
                                            bool protoNull    = proto is null;
                                            bool protoPhantom = proto is not null && _phantomProtoStubs.Contains(proto);
                                            // Always log every FoodData entry so we can diagnose.
                                            progress?.Report($"        FoodData: FoodProto={(proto?.ToString() ?? "NULL")} null={protoNull} phantom={protoPhantom}");
                                            // Remove entries where FoodProto is null (mod stripped) or phantom.
                                            return !protoNull && !protoPhantom;
                                        })
                                        .ToArray();

                            int removed = foodTypeList.Count - filtered.Length;
                            if (removed > 0)
                            {
                                var newArr = Array.CreateInstance(elemType, filtered.Length);
                                for (int i = 0; i < filtered.Length; i++)
                                    newArr.SetValue(filtered[i], i);
                                fiFoodTypes.SetValue(cat, CreateImmutableArray(elemType, newArr));
                                arrayRemoved += removed;
                                progress?.Report($"      FoodTypes rebuilt: removed {removed} bad FoodData entry/entries.");
                            }
                        }

                        // Rebuild m_foodCategories if any category entry was dropped or null.
                        if (categoriesNeedRebuild && catElemType is not null)
                        {
                            var validCats = categoryList
                                .Where(c =>
                                {
                                    if (c is null) return false;
                                    var cp = fiCatProto?.GetValue(c);
                                    return cp is not null && !_phantomProtoStubs.Contains(cp);
                                })
                                .ToArray();
                            var newCatArr = Array.CreateInstance(catElemType, validCats.Length);
                            for (int i = 0; i < validCats.Length; i++)
                                newCatArr.SetValue(validCats[i], i);
                            fiCategories.SetValue(obj, CreateImmutableArray(catElemType, newCatArr));
                            progress?.Report($"    m_foodCategories rebuilt: {categoryList.Count} → {validCats.Length} category/categories.");
                        }
                    }
                }

                if (mapRemoved > 0 || arrayRemoved > 0)
                {
                    settlementsFixed++;
                    totalMapRemoved  += mapRemoved;
                    totalArrayRemoved += arrayRemoved;
                    progress?.Report(
                        $"  Settlement food cleanup: removed {mapRemoved} phantom key(s) from m_foodTypesMap, " +
                        $"{arrayRemoved} phantom FoodData element(s) from m_foodCategories.FoodTypes.");
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"  FixSettlementPhantomFoodData error on {obj?.GetType().Name ?? "?"}: {ex.Message}");
            }
        }

        if (settlementsFixed == 0)
            progress?.Report("  FixSettlementPhantomFoodData: no phantom food protos found in any settlement.");
        else
            progress?.Report(
                $"  FixSettlementPhantomFoodData: cleaned {settlementsFixed} settlement(s) — " +
                $"{totalMapRemoved} map key(s) + {totalArrayRemoved} FoodTypes element(s) removed " +
                $"(prevents onNewDay crash and 0/0 unity bonuses).");
    }

    /// <summary>
    /// Removes null-Product machine input/output buffer entries that cause
    /// <c>Machine.initSelf → rebuildInputBuffers → ClearAndDestroyBuffer → buffer.Product.Type</c>
    /// NullReferenceException on load.
    ///
    /// When a mod is removed its products come back as null from ReadWeakProtoRef (rather than
    /// phantom stubs), so those ProductBuffer slots have Product == null.  rebuildInputBuffers
    /// iterates every buffer and calls ClearAndDestroyBuffer, which immediately dereferences
    /// buffer.Product.Type — crashing on the null.  Removing the null-Product buffers before
    /// serialisation lets initSelf complete cleanly; any contained quantity is simply lost (it
    /// was an unresolvable mod product anyway).
    /// </summary>
    private void FixMachineBuffersNullProduct(object resolver, IProgress<string>? progress)
    {
        var tMachine = AssemblyLoader.FindType("Mafi.Core.Factory.Machines.Machine");
        if (tMachine is null)
        {
            progress?.Report("  FixMachineBuffersNullProduct: Machine type not found — skipping.");
            return;
        }

        // LystStruct<T> is a struct; its backing fields are m_items (T[]) and Count (int).
        // ProductBuffer.Product is an auto-property → backing field <Product>k__BackingField.
        // Machines live in EntitiesManager.m_entitiesLinear, not in the top-level resolver
        // object list — mirror the same look-up pattern used by StripNullProtoEntitiesFromManagers.
        var tEntitiesManager = AssemblyLoader.FindType("Mafi.Core.Entities.EntitiesManager");
        if (tEntitiesManager is null)
        {
            progress?.Report("  FixMachineBuffersNullProduct: EntitiesManager type not found — skipping.");
            return;
        }

        var resolverType = resolver.GetType();
        var fiResolved = FindFieldDeep(resolverType, "m_resolvedObjects");
        if (fiResolved is null)
        {
            progress?.Report("  FixMachineBuffersNullProduct: m_resolvedObjects not found — skipping.");
            return;
        }

        var resolvedObjs = fiResolved.GetValue(resolver) as System.Collections.IEnumerable;
        object? entitiesManager = null;
        if (resolvedObjs is not null)
        {
            foreach (var ro in resolvedObjs)
            {
                if (ro is not null && tEntitiesManager.IsAssignableFrom(ro.GetType()))
                { entitiesManager = ro; break; }
            }
        }

        if (entitiesManager is null)
        {
            progress?.Report("  FixMachineBuffersNullProduct: EntitiesManager not found in resolved objects — skipping.");
            return;
        }

        var fiLinear = FindFieldDeep(entitiesManager.GetType(), "m_entitiesLinear");
        if (fiLinear is null)
        {
            progress?.Report("  FixMachineBuffersNullProduct: m_entitiesLinear not found — skipping.");
            return;
        }

        var entitiesLinear = fiLinear.GetValue(entitiesManager) as System.Collections.IEnumerable;
        if (entitiesLinear is null)
        {
            progress?.Report("  FixMachineBuffersNullProduct: m_entitiesLinear is null — skipping.");
            return;
        }

        var allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        FieldInfo? fiInputBuffers  = null;
        FieldInfo? fiOutputBuffers = null;

        int totalMachines = 0;
        int totalBuffersRemoved = 0;

        // LystStruct field infos are per-concrete-type; cache separately for input vs output.
        FieldInfo? fiLystItemsInput  = null;
        FieldInfo? fiLystCountInput  = null;
        FieldInfo? fiProductBackingInput = null;
        FieldInfo? fiLystItemsOutput = null;
        FieldInfo? fiLystCountOutput = null;
        FieldInfo? fiProductBackingOutput = null;

        foreach (var entity in entitiesLinear)
        {
            if (entity is null) continue;
            if (!tMachine.IsAssignableFrom(entity.GetType())) continue;
            totalMachines++;

            try
            {
                // Resolve field infos once from the first Machine instance.
                if (fiInputBuffers is null)
                {
                    fiInputBuffers  = FindFieldDeep(entity.GetType(), "m_inputBuffers");
                    fiOutputBuffers = FindFieldDeep(entity.GetType(), "m_outputBuffers");
                }
                if (fiInputBuffers is null || fiOutputBuffers is null) break;

                int removed = 0;
                removed += CleanLystStructBuffers(entity, fiInputBuffers,
                    ref fiLystItemsInput, ref fiLystCountInput, ref fiProductBackingInput, allFlags);
                removed += CleanLystStructBuffers(entity, fiOutputBuffers,
                    ref fiLystItemsOutput, ref fiLystCountOutput, ref fiProductBackingOutput, allFlags);

                if (removed > 0)
                {
                    totalBuffersRemoved += removed;
                    progress?.Report($"    Machine {entity.GetType().Name} @{entity.GetHashCode():X}: removed {removed} null-Product buffer(s).");
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"  FixMachineBuffersNullProduct error on {entity.GetType().Name}: {ex.Message}");
            }
        }

        if (totalBuffersRemoved == 0)
            progress?.Report($"  FixMachineBuffersNullProduct: no null-Product buffers found ({totalMachines} machines scanned).");
        else
            progress?.Report(
                $"  FixMachineBuffersNullProduct: removed {totalBuffersRemoved} null-Product buffer(s) across " +
                $"{totalMachines} machine(s) (prevents initSelf NRE on rebuildInputBuffers).");
    }

    /// <summary>Removes elements from a LystStruct-typed field on <paramref name="owner"/> where the
    /// element's <c>Product</c> backing field is null or a phantom stub.</summary>
    private int CleanLystStructBuffers(
        object owner, FieldInfo fiLystStructField,
        ref FieldInfo? fiLystItems, ref FieldInfo? fiLystCount, ref FieldInfo? fiProductBacking,
        BindingFlags allFlags)
    {
        // LystStruct<T> is a struct — GetValue boxes it; we must SetValue the modified box back.
        var boxedLyst = fiLystStructField.GetValue(owner);
        if (boxedLyst is null) return 0;

        var lystType = boxedLyst.GetType();

        if (fiLystItems is null)
            fiLystItems = lystType.GetField("m_items", allFlags);
        if (fiLystCount is null)
            fiLystCount = lystType.GetField("<Count>k__BackingField", allFlags)
                       ?? lystType.GetField("Count", allFlags); // fallback

        if (fiLystItems is null || fiLystCount is null) return 0;

        var itemsArr = fiLystItems.GetValue(boxedLyst) as Array;
        var countObj = fiLystCount.GetValue(boxedLyst);
        if (itemsArr is null || countObj is null) return 0;

        int count = (int)countObj;
        if (count <= 0) return 0;

        // Resolve ProductBuffer.Product backing field from the first non-null element.
        if (fiProductBacking is null)
        {
            for (int i = 0; i < count; i++)
            {
                var elem = itemsArr.GetValue(i);
                if (elem is null) continue;
                fiProductBacking = FindFieldDeep(elem.GetType(), "<Product>k__BackingField");
                break;
            }
        }
        if (fiProductBacking is null) return 0;

        // Compact the array: keep only elements whose Product is non-null and non-phantom.
        int writeIdx = 0;
        int removed  = 0;
        for (int i = 0; i < count; i++)
        {
            var elem = itemsArr.GetValue(i);
            if (elem is null) { removed++; continue; }
            var product = fiProductBacking.GetValue(elem);
            if (product is null || (_phantomProtoStubs is not null && _phantomProtoStubs.Contains(product)))
            {
                removed++;
                continue;
            }
            if (writeIdx != i)
                itemsArr.SetValue(elem, writeIdx);
            writeIdx++;
        }

        if (removed == 0) return 0;

        // Zero out the tail slots so GC can collect them.
        for (int i = writeIdx; i < count; i++)
            itemsArr.SetValue(null, i);

        // Write updated Count back into the boxed struct, then set the struct back on the owner.
        fiLystCount.SetValue(boxedLyst, writeIdx);
        fiLystStructField.SetValue(owner, boxedLyst);
        return removed;
    }

    // ─────────────────────────────────────────────────────────────────────
    // FixAssetTransactionManagerNullProductBuffers
    //
    // AssetTransactionManager.initSelf iterates m_providerBuffers (Lyst<GlobalBuffer>)
    // and calls m_productsManager.GetStatsFor(item.Buffer.Product). When mod products
    // are stripped, GlobalBuffer.Buffer can be null, or Buffer.Product is null/phantom,
    // causing GetStatsFor(null) → NullReferenceException.
    //
    // GlobalBuffer is a private readonly struct (Buffer, Priority, Entity). We can't
    // mutate it in place, but we don't need to — we just compact the Lyst<GlobalBuffer>'s
    // backing array (m_items, m_size) and drop bad entries entirely. This is the same
    // approach used for LystStruct buffers.
    // ─────────────────────────────────────────────────────────────────────
    private void FixAssetTransactionManagerNullProductBuffers(object resolver, IProgress<string>? progress)
    {
        var tAtm = AssemblyLoader.FindType("Mafi.Core.Economy.AssetTransactionManager");
        if (tAtm is null)
        {
            progress?.Report("  FixATMNullProductBuffers: AssetTransactionManager type not found — skipping.");
            return;
        }

        var fiResolved = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
        if (fiResolved?.GetValue(resolver) is not System.Collections.IEnumerable resolved)
        {
            progress?.Report("  FixATMNullProductBuffers: m_resolvedObjects not found — skipping.");
            return;
        }

        object? atm = null;
        foreach (var obj in resolved)
        {
            if (obj is not null && tAtm.IsAssignableFrom(obj.GetType()))
            {
                atm = obj;
                break;
            }
        }
        if (atm is null)
        {
            progress?.Report("  FixATMNullProductBuffers: AssetTransactionManager not found in resolver — skipping.");
            return;
        }

        const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        int totalRemoved = 0;
        totalRemoved += compactGlobalBufferLyst(atm, "m_providerBuffers", allInst, progress);
        totalRemoved += compactGlobalBufferLyst(atm, "m_receiverBuffers", allInst, progress);
        progress?.Report($"  FixATMNullProductBuffers: removed {totalRemoved} bad GlobalBuffer entry(ies) from AssetTransactionManager.");
    }

    private int compactGlobalBufferLyst(object atm, string fieldName, BindingFlags allInst, IProgress<string>? progress)
    {
        var fi = FindFieldDeep(atm.GetType(), fieldName);
        var lyst = fi?.GetValue(atm);
        if (lyst is null) return 0;

        var lystType = lyst.GetType();
        var fiItems = FindFieldDeep(lystType, "m_items");
        var fiSize  = FindFieldDeep(lystType, "m_size");
        if (fiItems is null || fiSize is null) return 0;

        if (fiItems.GetValue(lyst) is not System.Array items) return 0;
        int size = (int)(fiSize.GetValue(lyst) ?? 0);
        if (size <= 0) return 0;

        // GlobalBuffer is a value-type with readonly fields Buffer (IProductBuffer),
        // Priority (int), Entity (Option<IEntity>). We need its Buffer field reflected.
        var elemType = items.GetType().GetElementType();
        if (elemType is null) return 0;
        var fiGbBuffer = FindFieldDeep(elemType, "Buffer");
        if (fiGbBuffer is null) return 0;

        // Cache ProductBuffer.<Product>k__BackingField lazily, since IProductBuffer
        // implementations vary. We'll resolve per-instance.
        int writeIdx = 0;
        int removed  = 0;
        for (int i = 0; i < size; i++)
        {
            var gb = items.GetValue(i);
            bool drop = false;
            if (gb is null) { drop = true; }
            else
            {
                var buf = fiGbBuffer.GetValue(gb);
                if (buf is null) { drop = true; }
                else
                {
                    var fiBufProduct = FindFieldDeep(buf.GetType(), "<Product>k__BackingField")
                                        ?? FindFieldDeep(buf.GetType(), "m_product");
                    object? product = fiBufProduct?.GetValue(buf);
                    if (product is null) drop = true;
                    else if (_phantomProtoStubs is not null && _phantomProtoStubs.Contains(product)) drop = true;
                }
            }

            if (drop) { removed++; continue; }
            if (writeIdx != i) items.SetValue(gb, writeIdx);
            writeIdx++;
        }

        if (removed == 0) return 0;

        // Clear tail slots for GC.
        for (int i = writeIdx; i < size; i++)
            items.SetValue(elemType.IsValueType ? System.Activator.CreateInstance(elemType) : null, i);

        fiSize.SetValue(lyst, writeIdx);
        progress?.Report($"    {fieldName}: removed {removed} bad entry(ies), {writeIdx} remain.");
        return removed;
    }

    // ─────────────────────────────────────────────────────────────────────
    // FixVehicleBuffersRegistryPhantomEntries
    //
    // VehicleBuffersRegistry holds:
    //   - Dict<ProductProto, RegisteredBuffers> m_registeredBuffersPerProduct
    //   - Dict<IStaticEntity, RegisteredBuffersPerEntity> m_registeredBuffersPerEntity
    // Both contain Lyst<RegisteredInputBuffer> and Lyst<RegisteredOutputBuffer>.
    // RegisteredOutputBuffer.initAll() → Position2f = Entity.Position2f (NRE if Entity null,
    // i.e. the static entity was stripped) and m_logisticsBuffer = Buffer as LogisticsBuffer
    // (NRE if Buffer null because the IProductBuffer instance was a phantom-product owner).
    //
    // Strategy:
    //   1. m_registeredBuffersPerProduct: drop entries whose KEY is a phantom ProductProto;
    //      for survivors, compact InputBuffers/OutputBuffers Lyst by removing items whose
    //      Entity, Buffer, or Buffer.Product is null/phantom.
    //   2. m_registeredBuffersPerEntity: drop entries whose KEY entity is null (resolver-stripped);
    //      for survivors, compact the same way.
    //   m_registeredInputBuffers / m_registeredOutputBuffers are [DoNotSave] and rebuilt at load.
    // ─────────────────────────────────────────────────────────────────────
    private void FixVehicleBuffersRegistryPhantomEntries(object resolver, IProgress<string>? progress)
    {
        var tVbr = AssemblyLoader.FindType("Mafi.Core.Vehicles.VehicleBuffersRegistry");
        if (tVbr is null)
        {
            progress?.Report("  FixVBRPhantom: VehicleBuffersRegistry type not found — skipping.");
            return;
        }

        var fiResolved = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
        if (fiResolved?.GetValue(resolver) is not System.Collections.IEnumerable resolved)
        {
            progress?.Report("  FixVBRPhantom: m_resolvedObjects not found — skipping.");
            return;
        }

        object? vbr = null;
        foreach (var obj in resolved)
        {
            if (obj is not null && tVbr.IsAssignableFrom(obj.GetType()))
            {
                vbr = obj;
                break;
            }
        }
        if (vbr is null)
        {
            progress?.Report("  FixVBRPhantom: VehicleBuffersRegistry not found in resolver — skipping.");
            return;
        }

        const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        int totalDictRemoved   = 0;
        int totalBuffersRemoved = 0;

        // ── 1. m_registeredBuffersPerProduct: Dict<ProductProto, RegisteredBuffers>
        var fiPerProduct = FindFieldDeep(vbr.GetType(), "m_registeredBuffersPerProduct");
        var perProduct = fiPerProduct?.GetValue(vbr);
        if (perProduct is not null)
        {
            var (keysToDelete, survivors) = scanDictForPhantomKeysAndSurvivors(perProduct, allInst);
            // Remove phantom-keyed entries.
            var miRemove = perProduct.GetType().GetMethod("Remove",
                new[] { perProduct.GetType().GetGenericArguments()[0] });
            foreach (var k in keysToDelete)
            {
                try { miRemove?.Invoke(perProduct, new[] { k }); totalDictRemoved++; }
                catch { }
            }
            // Compact survivor RegisteredBuffers.InputBuffers / OutputBuffers.
            foreach (var rb in survivors)
            {
                if (rb is null) continue;
                totalBuffersRemoved += compactRegisteredBufferLyst(rb, "InputBuffers", allInst);
                totalBuffersRemoved += compactRegisteredBufferLyst(rb, "OutputBuffers", allInst);
            }
            progress?.Report($"    m_registeredBuffersPerProduct: removed {keysToDelete.Count} phantom-keyed entry(ies), {survivors.Count} survived.");
        }

        // ── 2. m_registeredBuffersPerEntity: Dict<IStaticEntity, RegisteredBuffersPerEntity>
        var fiPerEntity = FindFieldDeep(vbr.GetType(), "m_registeredBuffersPerEntity");
        var perEntity = fiPerEntity?.GetValue(vbr);
        if (perEntity is not null)
        {
            // Key here is IStaticEntity, not a ProductProto, so phantom check is different.
            // We drop entries whose key is null. Stripped-entity references should normally
            // already be removed by entity-stripping passes, but extra safety here.
            var (nullKeys, survivors2) = scanDictForNullKeysAndSurvivors(perEntity, allInst);
            var miRemove2 = perEntity.GetType().GetMethod("Remove",
                new[] { perEntity.GetType().GetGenericArguments()[0] });
            foreach (var k in nullKeys)
            {
                try { miRemove2?.Invoke(perEntity, new[] { k }); totalDictRemoved++; }
                catch { }
            }
            foreach (var rbpe in survivors2)
            {
                if (rbpe is null) continue;
                totalBuffersRemoved += compactRegisteredBufferLyst(rbpe, "InputBuffers", allInst);
                totalBuffersRemoved += compactRegisteredBufferLyst(rbpe, "OutputBuffers", allInst);
            }
            progress?.Report($"    m_registeredBuffersPerEntity: removed {nullKeys.Count} null-keyed entry(ies), {survivors2.Count} survived.");
        }

        progress?.Report($"  FixVBRPhantom: dict removals={totalDictRemoved}, RegisteredBuffer removals={totalBuffersRemoved}.");
    }

    // Walk a Mafi Dict<TKey,TValue> as IEnumerable<KeyValuePair<,>> (Mafi Dict is enumerable).
    // Returns (phantom-product keys, all surviving values).
    private (List<object> keys, List<object?> survivors) scanDictForPhantomKeysAndSurvivors(object dict, BindingFlags allInst)
    {
        var keys = new List<object>();
        var survivors = new List<object?>();
        if (dict is not System.Collections.IEnumerable e) return (keys, survivors);

        var ga = dict.GetType().GetGenericArguments();
        var kvpType = typeof(System.Collections.Generic.KeyValuePair<,>).MakeGenericType(ga);
        var pKey = kvpType.GetProperty("Key", allInst);
        var pVal = kvpType.GetProperty("Value", allInst);
        if (pKey is null || pVal is null) return (keys, survivors);

        foreach (var kvp in e)
        {
            if (kvp is null) continue;
            var key = pKey.GetValue(kvp);
            var val = pVal.GetValue(kvp);
            bool isPhantom = key is not null && _phantomProtoStubs is not null && _phantomProtoStubs.Contains(key);
            bool keyIsNull = key is null;
            if (isPhantom || keyIsNull)
            {
                if (key is not null) keys.Add(key);
            }
            else
            {
                survivors.Add(val);
            }
        }
        return (keys, survivors);
    }

    private (List<object> keys, List<object?> survivors) scanDictForNullKeysAndSurvivors(object dict, BindingFlags allInst)
    {
        // Same pattern but only flags null keys (entity-keyed dict).
        var keys = new List<object>();
        var survivors = new List<object?>();
        if (dict is not System.Collections.IEnumerable e) return (keys, survivors);

        var ga = dict.GetType().GetGenericArguments();
        var kvpType = typeof(System.Collections.Generic.KeyValuePair<,>).MakeGenericType(ga);
        var pKey = kvpType.GetProperty("Key", allInst);
        var pVal = kvpType.GetProperty("Value", allInst);
        if (pKey is null || pVal is null) return (keys, survivors);

        foreach (var kvp in e)
        {
            if (kvp is null) continue;
            var key = pKey.GetValue(kvp);
            var val = pVal.GetValue(kvp);
            if (key is null) { /* can't remove a null key from Mafi Dict; skip */ continue; }
            survivors.Add(val);
        }
        return (keys, survivors);
    }

    // Compact a Lyst<RegisteredInputBuffer> / Lyst<RegisteredOutputBuffer> stored as a
    // reference field on the owner. Drops entries whose Entity, Buffer, or Buffer.Product
    // is null/phantom — which is exactly what causes RegisteredOutputBuffer.initAll() NRE.
    private int compactRegisteredBufferLyst(object owner, string fieldName, BindingFlags allInst)
    {
        var fi = FindFieldDeep(owner.GetType(), fieldName);
        var lyst = fi?.GetValue(owner);
        if (lyst is null) return 0;

        var lystType = lyst.GetType();
        var fiItems = FindFieldDeep(lystType, "m_items");
        var fiSize  = FindFieldDeep(lystType, "m_size");
        if (fiItems is null || fiSize is null) return 0;

        if (fiItems.GetValue(lyst) is not System.Array items) return 0;
        int size = (int)(fiSize.GetValue(lyst) ?? 0);
        if (size <= 0) return 0;

        int writeIdx = 0;
        int removed  = 0;
        for (int i = 0; i < size; i++)
        {
            var rb = items.GetValue(i);
            bool drop = false;
            if (rb is null) { drop = true; }
            else
            {
                var rbType = rb.GetType();
                var fiEntity = FindFieldDeep(rbType, "<Entity>k__BackingField");
                var fiBuffer = FindFieldDeep(rbType, "Buffer");
                var ent = fiEntity?.GetValue(rb);
                var buf = fiBuffer?.GetValue(rb);
                if (ent is null || buf is null) { drop = true; }
                else
                {
                    var fiProd = FindFieldDeep(buf.GetType(), "<Product>k__BackingField")
                                  ?? FindFieldDeep(buf.GetType(), "m_product");
                    var prod = fiProd?.GetValue(buf);
                    if (prod is null) drop = true;
                    else if (_phantomProtoStubs is not null && _phantomProtoStubs.Contains(prod)) drop = true;
                }
            }

            if (drop) { removed++; continue; }
            if (writeIdx != i) items.SetValue(rb, writeIdx);
            writeIdx++;
        }

        if (removed == 0) return 0;
        for (int i = writeIdx; i < size; i++) items.SetValue(null, i);
        fiSize.SetValue(lyst, writeIdx);
        return removed;
    }
}
