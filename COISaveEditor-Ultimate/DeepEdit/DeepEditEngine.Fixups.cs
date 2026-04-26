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
    private void PrepareObjectsForReserialization(object resolver, IProgress<string>? progress)
    {
        var resolverType = resolver.GetType();
        var fiResolved = FindFieldDeep(resolverType, "m_resolvedObjects");
        if (fiResolved is null) return;

        var resolved = fiResolved.GetValue(resolver) as System.Collections.IEnumerable;
        if (resolved is null) return;

        var tTerrainManager = AssemblyLoader.FindType("Mafi.Core.Terrain.TerrainManager");
        var tUnlockedProtosDb = AssemblyLoader.FindType("Mafi.Core.Prototypes.UnlockedProtosDb");

        if (tTerrainManager is null)
            progress?.Report("    Could not locate TerrainManager type — skipping terrain fixup.");
        if (tUnlockedProtosDb is null)
            progress?.Report("    Could not locate UnlockedProtosDb type — skipping unlock fixup.");

        foreach (var obj in resolved)
        {
            if (obj is null) continue;
            var objType = obj.GetType();

            if (tTerrainManager is not null && objType == tTerrainManager)
                ReconstitutTerrainDataArrays(obj, progress);

            if (tUnlockedProtosDb is not null && tUnlockedProtosDb.IsAssignableFrom(objType))
                RunUnlockedProtosDbInitAfterLoad(obj, progress);
        }
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
    /// Adjusts phantom proto IDs before re-serialisation.
    /// Core game protos (Mafi.Core, Mafi.Base, Mafi.TrainsDlc) keep their original save-file IDs
    /// so the game's own ProtosDb can resolve them on load.
    /// Protos from removed mods get unique placeholder IDs to avoid duplicate-key exceptions
    /// in the game's SlimIdManager.
    /// </summary>
    private void NullifyPhantomProtoIds(IProgress<string>? progress)
    {
        if (_phantomProtoStubs is null || _phantomProtoStubs.Count == 0)
        {
            progress?.Report("  No phantom proto stubs to clean.");
            return;
        }

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

        foreach (var stub in _phantomProtoStubs)
        {
            try
            {
                string? originalId = null;
                var idObj = fiProtoId.GetValue(stub);
                if (idObj is not null)
                {
                    // Proto.ID has a ToString() or Value property that gives the string ID.
                    var valueProp = idObj.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    originalId = (valueProp?.GetValue(idObj) as string) ?? idObj.ToString();
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
        progress?.Report($"  Phantom proto IDs: {keptOriginal} kept original, {hijackedToVanilla} hijacked to a vanilla proto ID of the same type, {reassigned} assigned placeholder IDs (these will crash the game if referenced).");
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
