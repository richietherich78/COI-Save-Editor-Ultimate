using System.Reflection;

namespace COISaveEditorUltimate.DeepEdit;

public sealed partial class DeepEditEngine
{
    // ── Object filter / strip ─────────────────────────────────────────────

    /// <summary>
    /// Removes all objects in the DependencyResolver whose runtime assembly
    /// name matches an assembly belonging to a removed mod.
    /// </summary>
    private (int removed, List<string> types) StripRemovedModObjects(
        object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        int removed = 0;
        var removedTypes = new List<string>();

        var resolverType = resolver.GetType();

        void StripLyst(string fieldName)
        {
            var fi = FindFieldDeep(resolverType, fieldName);
            if (fi is null) return;
            var lyst = fi.GetValue(resolver);
            if (lyst is null) return;

            var items = (lyst as System.Collections.IEnumerable)?.Cast<object>().ToList();
            if (items is null) return;

            var toRemove = items.Where(o => ShouldStrip(o?.GetType(), stripAssemblies)).ToList();
            if (toRemove.Count == 0) return;

            foreach (var o in toRemove)
            {
                removedTypes.Add(o.GetType().FullName ?? o.GetType().Name);
                progress?.Report($"    Stripping {o.GetType().FullName}");
                removed++;
                var removeMethod = lyst.GetType().GetMethod("Remove");
                removeMethod?.Invoke(lyst, new[] { o });
            }
        }

        void StripDict(string fieldName)
        {
            var fi = FindFieldDeep(resolverType, fieldName);
            if (fi is null) return;
            var dict = fi.GetValue(resolver);
            if (dict is null) return;

            var dictType = dict.GetType();
            var keys = (dict as System.Collections.IEnumerable)?
                       .Cast<object>()
                       .Select(kv =>
                       {
                           var kvType = kv.GetType();
                           var key = kvType.GetProperty("Key")?.GetValue(kv);
                           var val = kvType.GetProperty("Value")?.GetValue(kv);
                           return (key: key as Type, val);
                       })
                       .Where(pair => pair.key is not null)
                       .ToList();

            if (keys is null) return;
            var removeMethod = dictType.GetMethod("Remove", new[] { typeof(Type) });

            foreach (var (key, val) in keys)
            {
                if (key is null) continue;
                if (ShouldStrip(val?.GetType(), stripAssemblies) ||
                    ShouldStrip(key, stripAssemblies))
                {
                    removedTypes.Add($"[{key.FullName}] → {val?.GetType().FullName}");
                    progress?.Report($"    Stripping registered [{key.Name}] → {val?.GetType()?.Name}");
                    removeMethod?.Invoke(dict, new object[] { key! });
                    removed++;
                }
            }
        }

        StripLyst("m_resolvedObjects");
        StripLyst("m_instancedToBeDisposed");
        StripDict("m_resolvedInstancesByRealType");
        StripDict("m_resolvedInstancesByRegisteredType");

        return (removed, removedTypes);
    }

    /// <summary>
    /// Removes specific object instances from all resolver collections.
    /// </summary>
    private (int removed, List<string> types) StripSpecificObjects(
        object resolver, HashSet<object> failedObjects, IProgress<string>? progress)
    {
        int removed = 0;
        var removedTypes = new List<string>();
        var resolverType = resolver.GetType();

        void StripFromLyst(string fieldName)
        {
            var fi = FindFieldDeep(resolverType, fieldName);
            if (fi is null) return;
            var lyst = fi.GetValue(resolver);
            if (lyst is null) return;

            var items = (lyst as System.Collections.IEnumerable)?.Cast<object>().ToList();
            if (items is null) return;

            var removeMethod = lyst.GetType().GetMethod("Remove");
            foreach (var item in items)
            {
                if (item is not null && failedObjects.Contains(item))
                {
                    var typeName = item.GetType().FullName ?? item.GetType().Name;
                    removedTypes.Add($"[failed] {typeName}");
                    progress?.Report($"    Stripping failed object: {typeName}");
                    removeMethod?.Invoke(lyst, new[] { item });
                    removed++;
                }
            }
        }

        void StripFromDict(string fieldName)
        {
            var fi = FindFieldDeep(resolverType, fieldName);
            if (fi is null) return;
            var dict = fi.GetValue(resolver);
            if (dict is null) return;

            var keys = (dict as System.Collections.IEnumerable)?
                .Cast<object>()
                .Select(kv =>
                {
                    var kvType = kv.GetType();
                    var key = kvType.GetProperty("Key")?.GetValue(kv);
                    var val = kvType.GetProperty("Value")?.GetValue(kv);
                    return (key: key as Type, val);
                })
                .Where(pair => pair.key is not null)
                .ToList();

            if (keys is null) return;
            var removeMethod = dict.GetType().GetMethod("Remove", new[] { typeof(Type) });

            foreach (var (key, val) in keys)
            {
                if (key is null || val is null) continue;
                if (failedObjects.Contains(val))
                {
                    removedTypes.Add($"[failed] [{key.Name}] → {val.GetType().Name}");
                    progress?.Report($"    Stripping failed [{key.Name}] → {val.GetType().Name}");
                    removeMethod?.Invoke(dict, new object[] { key! });
                    removed++;
                }
            }
        }

        StripFromLyst("m_resolvedObjects");
        StripFromLyst("m_instancedToBeDisposed");
        StripFromDict("m_resolvedInstancesByRealType");
        StripFromDict("m_resolvedInstancesByRegisteredType");

        return (removed, removedTypes);
    }

    /// <summary>
    /// Final safety sweep: directly purges all objects whose concrete runtime type belongs
    /// to a removed-mod assembly from <c>m_resolvedObjects</c> (and its sibling collections).
    /// Uses direct backing-array manipulation to bypass any <c>Remove(T)</c> reflection
    /// issues that may cause <see cref="StripSpecificObjects"/> to silently fail.
    /// </summary>
    private void PurgeModAssemblyObjectsFromResolver(
        object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        if (stripAssemblies.Count == 0) return;

        int purged = 0;
        int logBudget = 200;

        void PurgeLystField(string fieldName)
        {
            var fi = FindFieldDeep(resolver.GetType(), fieldName);
            if (fi is null)
            {
                progress?.Report($"  [FinalPurge] Field '{fieldName}' not found on {resolver.GetType().Name}");
                return;
            }
            var lyst = fi.GetValue(resolver);
            if (lyst is null) { progress?.Report($"  [FinalPurge] '{fieldName}' is null"); return; }

            progress?.Report($"  [FinalPurge] '{fieldName}' type={lyst.GetType().Name}");

            // Enumerate via IEnumerable to always see what's there.
            if (lyst is not System.Collections.IEnumerable enumerable) return;
            var allItems = enumerable.Cast<object>().Where(x => x is not null).ToList();
            var toRemove = allItems.Where(x => ShouldStrip(x.GetType(), stripAssemblies)).ToList();

            if (toRemove.Count == 0)
            {
                progress?.Report($"  [FinalPurge] '{fieldName}': {allItems.Count} item(s), 0 from removed-mod assemblies.");
                return;
            }

            foreach (var item in toRemove)
                if (--logBudget > 0)
                    progress?.Report($"  [FinalPurge] FOUND in '{fieldName}': {item.GetType().FullName}");

            // Try backing-array compaction first (Lyst / LystMutableDuringIter).
            var lystType = lyst.GetType();
            var fiItems = lystType.GetField("m_items", BindingFlags.NonPublic | BindingFlags.Instance);
            var fiSize  = lystType.GetField("m_size",  BindingFlags.NonPublic | BindingFlags.Instance);
            if (fiItems != null && fiSize != null)
            {
                var arr  = fiItems.GetValue(lyst) as Array;
                var size = (int)(fiSize.GetValue(lyst) ?? 0);
                if (arr != null)
                {
                    var stripSet = new HashSet<object>(toRemove, ReferenceEqualityComparer.Instance);
                    int write = 0;
                    for (int i = 0; i < size; i++)
                    {
                        var item = arr.GetValue(i);
                        if (item != null && stripSet.Contains(item)) { purged++; continue; }
                        arr.SetValue(item, write++);
                    }
                    for (int i = write; i < size; i++) arr.SetValue(null, i);
                    fiSize.SetValue(lyst, write);
                    progress?.Report($"  [FinalPurge] Removed {toRemove.Count} item(s) from '{fieldName}' via backing-array compaction.");
                    return;
                }
            }

            // Fallback: try Remove method.
            var removeMethod = lystType.GetMethods().FirstOrDefault(m =>
                m.Name == "Remove" && m.GetParameters().Length == 1);
            if (removeMethod != null)
            {
                foreach (var item in toRemove)
                    try { removeMethod.Invoke(lyst, new[] { item }); purged++; } catch { }
                progress?.Report($"  [FinalPurge] Removed {toRemove.Count} item(s) from '{fieldName}' via Remove().");
            }
            else
            {
                progress?.Report($"  [FinalPurge] WARNING: could not remove from '{fieldName}' — no Remove method and no m_items field.");
            }
        }

        void PurgeDictField(string fieldName)
        {
            var fi = FindFieldDeep(resolver.GetType(), fieldName);
            if (fi is null) return;
            var dict = fi.GetValue(resolver) as System.Collections.IEnumerable;
            if (dict is null) return;

            // Remove an entry if EITHER the key Type belongs to a stripped-mod assembly,
            // OR the value's runtime type does. The resolver's serializeData writes the
            // KEY as an AQN via WriteType BEFORE writing the value (DependencyResolver.cs
            // line 1603 in 0.8.2: `writer.WriteType(current4.Key)`), so a stripped-mod
            // KEY with a vanilla value still emits the offending mod AQN into the binary —
            // which is exactly the validator violation we've been chasing.
            var pairs = dict.Cast<object>()
                .Select(kv =>
                {
                    var kt = kv?.GetType();
                    return (key: kt?.GetProperty("Key")?.GetValue(kv), val: kt?.GetProperty("Value")?.GetValue(kv));
                })
                .Where(p => p.key is Type kType
                            && (ShouldStrip(kType, stripAssemblies)
                                || (p.val is not null && ShouldStrip(p.val.GetType(), stripAssemblies))))
                .ToList();

            if (pairs.Count == 0) return;
            var removeMethod = fi.GetValue(resolver)?.GetType().GetMethod("Remove", new[] { typeof(Type) });
            foreach (var (key, val) in pairs)
            {
                if (key is Type t)
                {
                    if (--logBudget > 0)
                    {
                        bool keyStripped = ShouldStrip(t, stripAssemblies);
                        bool valStripped = val is not null && ShouldStrip(val.GetType(), stripAssemblies);
                        string reason = (keyStripped, valStripped) switch
                        {
                            (true, true)  => "key+val",
                            (true, false) => "key",
                            (false, true) => "val",
                            _             => "?",
                        };
                        progress?.Report($"  [FinalPurge] Removing [{t.Name}]→{val?.GetType().Name ?? "<null>"} from {fieldName} (reason={reason})");
                    }
                    try { removeMethod?.Invoke(fi.GetValue(resolver), new object[] { t }); purged++; } catch { }
                }
            }
        }

        PurgeLystField("m_resolvedObjects");
        PurgeLystField("m_instancedToBeDisposed");
        PurgeDictField("m_resolvedInstancesByRealType");
        PurgeDictField("m_resolvedInstancesByRegisteredType");

        progress?.Report($"  [FinalPurge] Purged {purged} remaining mod-assembly object(s) from resolver collections.");
    }

    /// <summary>
    /// Last-line-of-defence diagnostic: enumerates every entry across all four
    /// resolver collections (<c>m_resolvedObjects</c>, <c>m_instancedToBeDisposed</c>,
    /// <c>m_resolvedInstancesByRealType</c>, <c>m_resolvedInstancesByRegisteredType</c>)
    /// and reports anything whose runtime AQN belongs to a removed-mod assembly.
    /// <para/>
    /// Called immediately before re-serialisation. The BlobWriter writes whatever it
    /// sees here, so anything reported is exactly what the validator will catch on
    /// output. Pure logging — does not modify state.
    /// </summary>
    internal static void DumpAnyRemainingModAssemblyObjectsInResolver(
        object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        if (stripAssemblies.Count == 0) return;
        var resolverType = resolver.GetType();
        int totalHits = 0;

        void DumpLyst(string fieldName)
        {
            try
            {
                var fi = FindFieldDeep(resolverType, fieldName);
                if (fi?.GetValue(resolver) is not System.Collections.IEnumerable lyst) return;
                int idx = 0;
                foreach (var item in lyst)
                {
                    if (item is null) { idx++; continue; }
                    var t = item.GetType();
                    if (ShouldStrip(t, stripAssemblies))
                    {
                        progress?.Report($"  [PreSerialiseDump] {fieldName}[{idx}] = {t.FullName} (asm={t.Assembly.GetName().Name})");
                        totalHits++;
                    }
                    idx++;
                }
            }
            catch (Exception ex) { progress?.Report($"  [PreSerialiseDump] {fieldName}: scan threw {ex.GetType().Name}: {ex.Message}"); }
        }

        void DumpDict(string fieldName)
        {
            try
            {
                var fi = FindFieldDeep(resolverType, fieldName);
                if (fi?.GetValue(resolver) is not System.Collections.IEnumerable dict) return;
                foreach (var kv in dict)
                {
                    if (kv is null) continue;
                    var kvT = kv.GetType();
                    var keyProp = kvT.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
                    var valProp = kvT.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    var key = keyProp?.GetValue(kv);
                    var val = valProp?.GetValue(kv);
                    if (val is null) continue;

                    var keyType = key as Type;
                    var valType = val.GetType();

                    bool keyHit = keyType is not null && ShouldStrip(keyType, stripAssemblies);
                    bool valHit = ShouldStrip(valType, stripAssemblies);

                    if (keyHit || valHit)
                    {
                        progress?.Report($"  [PreSerialiseDump] {fieldName}[{keyType?.FullName ?? key?.ToString() ?? "<null>"}] = {valType.FullName} (asm={valType.Assembly.GetName().Name}, keyHit={keyHit}, valHit={valHit})");
                        totalHits++;
                    }
                }
            }
            catch (Exception ex) { progress?.Report($"  [PreSerialiseDump] {fieldName}: scan threw {ex.GetType().Name}: {ex.Message}"); }
        }

        DumpLyst("m_resolvedObjects");
        DumpLyst("m_instancedToBeDisposed");
        DumpDict("m_resolvedInstancesByRealType");
        DumpDict("m_resolvedInstancesByRegisteredType");

        progress?.Report($"  [PreSerialiseDump] {totalHits} mod-assembly entry(ies) STILL in resolver collections at re-serialise time.");
    }

    /// <summary>
    /// Diagnostic: logs the type name of every object in m_resolvedObjects, numbered
    /// and sorted. Used to diff two runs and identify any extra/missing objects.
    /// Pure logging — does not modify state.
    /// </summary>
    internal static void LogAllResolvedObjectTypes(object resolver, IProgress<string>? progress, string label = "")
    {
        try
        {
            var fi = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
            if (fi?.GetValue(resolver) is not System.Collections.IEnumerable lyst) return;

            var types = new List<string>();
            foreach (var item in lyst)
                types.Add(item?.GetType().FullName ?? "<null>");

            var sorted = types.ToList();
            sorted.Sort(StringComparer.Ordinal);

            progress?.Report($"  [ResolvedTypeList{label}] count={types.Count}");
            for (int i = 0; i < sorted.Count; i++)
                progress?.Report($"  [ResolvedTypeList{label}] {i,3}: {sorted[i]}");
        }
        catch (Exception ex)
        {
            progress?.Report($"  [ResolvedTypeList{label}] scan threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs every object in m_resolvedObjects in INSERTION ORDER with its sequential index.
    /// Unlike LogAllResolvedObjectTypes (which sorts), this preserves the order BlobWriter
    /// will use to assign object IDs — essential for diagnosing ID-shift crashes.
    /// </summary>
    internal static void LogResolvedObjectsInOrder(object resolver, IProgress<string>? progress, string label = "")
    {
        try
        {
            var fi = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
            if (fi?.GetValue(resolver) is not System.Collections.IEnumerable lyst) return;

            int i = 0;
            progress?.Report($"  [ResolvedOrder{label}] (insertion order)");
            foreach (var item in lyst)
                progress?.Report($"  [ResolvedOrder{label}] {i++,3}: {item?.GetType().FullName ?? "<null>"}");
            progress?.Report($"  [ResolvedOrder{label}] total={i}");
        }
        catch (Exception ex)
        {
            progress?.Report($"  [ResolvedOrder{label}] scan threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Phase-2 new-type purge ────────────────────────────────────────────

    /// <summary>
    /// Returns a snapshot of all concrete runtime types currently in m_resolvedObjects.
    /// Call before Phase 2 (FinalizeLoading) to establish a baseline of types that were
    /// actually deserialized from the save file.
    /// </summary>
    private static HashSet<Type> SnapshotResolvedObjectTypes(object resolver)
    {
        var result = new HashSet<Type>();
        try
        {
            var fi = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
            if (fi?.GetValue(resolver) is not System.Collections.IEnumerable lyst) return result;
            foreach (var item in lyst)
                if (item is not null) result.Add(item.GetType());
        }
        catch { /* best-effort */ }
        return result;
    }

    /// <summary>
    /// Removes from m_resolvedObjects any object that (a) was added during Phase 2 and
    /// (b) has a concrete type that was NOT present in m_resolvedObjects before Phase 2.
    /// Such objects are vanilla managers newly introduced in the current game DLL version
    /// that didn't exist when the save file was originally created. Including them inflates
    /// the BlobWriter object-ID sequence, causing off-by-N back-references in later managers
    /// (e.g. TrainLinesManager CorruptedSaveException).
    /// </summary>
    private static void PurgePhase2AddedNewTypeObjects(
        object resolver, HashSet<Type> prePhase2Types, IProgress<string>? progress)
    {
        try
        {
            var fi = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
            if (fi?.GetValue(resolver) is not System.Collections.IEnumerable lyst) return;

            var allItems = lyst.Cast<object?>().ToList();
            var toRemove = new List<object>();
            foreach (var item in allItems)
            {
                if (item is null) continue;
                var t = item.GetType();
                // Only remove vanilla objects (not mod assemblies — those are handled elsewhere).
                // A type is "new to this DLL version" when it never appeared in the pre-Phase2 snapshot.
                if (!prePhase2Types.Contains(t) && !t.Assembly.GetName().Name!.StartsWith("COIExtended", StringComparison.OrdinalIgnoreCase))
                    toRemove.Add(item);
            }

            if (toRemove.Count == 0) return;

            var miRemove = lyst.GetType().GetMethod("Remove");
            foreach (var obj in toRemove)
            {
                progress?.Report($"  [Phase2NewTypePurge] Removing DLL-version-new type {obj.GetType().FullName}");
                miRemove?.Invoke(lyst, new[] { obj });
            }
            progress?.Report($"  [Phase2NewTypePurge] Removed {toRemove.Count} DLL-version-new object(s).");
        }
        catch (Exception ex)
        {
            progress?.Report($"  [Phase2NewTypePurge] Error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Targeted DLL-version delta removal ───────────────────────────────
    // Types added to m_resolvedObjects by Phase 2 in newer game DLL versions
    // that were NOT present when the save was originally created. Including them
    // shifts every subsequent BlobWriter object-ID by N, corrupting back-references
    // (symptom: TrainLinesManager CorruptedSaveException on load).
    // These objects are safe to remove — the game recreates them during its own
    // Phase 2 when loading the save.
    private static readonly string[] _knownDllVersionAddedTypes =
    {
        "Mafi.Core.Trains.TrainNetworksManager",  // added by 0.8.3 DLLs; absent in 0.8.2 saves
    };

    private static void RemoveKnownDllVersionAddedObjects(object resolver, IProgress<string>? progress)
    {
        try
        {
            var fi = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
            if (fi?.GetValue(resolver) is not System.Collections.IEnumerable lyst) return;

            var items = lyst.Cast<object?>().ToList();
            var miRemove = lyst.GetType().GetMethod("Remove");
            int removed = 0;
            foreach (var item in items)
            {
                if (item is null) continue;
                var fullName = item.GetType().FullName ?? "";
                if (Array.IndexOf(_knownDllVersionAddedTypes, fullName) >= 0)
                {
                    miRemove?.Invoke(lyst, new[] { item });
                    progress?.Report($"  [DllDeltaPurge] Removed version-added object: {fullName}");
                    removed++;
                }
            }
            if (removed > 0)
                progress?.Report($"  [DllDeltaPurge] Removed {removed} version-added object(s) from m_resolvedObjects.");
            else
                progress?.Report($"  [DllDeltaPurge] No version-added objects found (save already matches DLL version).");
        }
        catch (Exception ex)
        {
            progress?.Report($"  [DllDeltaPurge] Error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Null-proto entity stripping ───────────────────────────────────────

    private void StripNullProtoEntities(object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        var resolverType = resolver.GetType();
        var fiResolved = FindFieldDeep(resolverType, "m_resolvedObjects");
        if (fiResolved is null) return;

        var resolved = fiResolved.GetValue(resolver) as System.Collections.IEnumerable;
        if (resolved is null) return;

        var nullProtoObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var tProto = AssemblyLoader.FindType("Mafi.Core.Prototypes.Proto");

        foreach (var obj in resolved)
        {
            if (obj is null) continue;
            var t = obj.GetType();

            // Only strip objects whose CLASS is from a removed mod assembly.
            // Vanilla objects with phantom protos are left alone — the game handles
            // null proto warnings gracefully and stripping them removes base-game content.
            if (!ShouldStrip(t, stripAssemblies)) continue;

            for (var cur = t; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                foreach (var fi in cur.GetFields(allFlags | BindingFlags.DeclaredOnly))
                {
                    if (fi.FieldType.IsValueType) continue;

                    bool isProtoField = fi.Name.Equals("m_proto", StringComparison.Ordinal)
                        || fi.Name.Equals("m_prototype", StringComparison.OrdinalIgnoreCase)
                        || fi.Name.Equals("Prototype", StringComparison.Ordinal)
                        || (fi.Name.StartsWith("<", StringComparison.Ordinal)
                            && fi.Name.EndsWith(">k__BackingField", StringComparison.Ordinal)
                            && fi.Name.IndexOf("Proto", 1, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (tProto is not null && tProto.IsAssignableFrom(fi.FieldType));

                    if (!isProtoField) continue;

                    try
                    {
                        var protoVal = fi.GetValue(obj);
                        if (protoVal is null || (_phantomProtoStubs is not null && _phantomProtoStubs.Contains(protoVal)))
                        {
                            nullProtoObjects.Add(obj);
                        }
                    }
                    catch { }
                }

                if (nullProtoObjects.Contains(obj)) break;
            }
        }

        if (nullProtoObjects.Count == 0)
        {
            progress?.Report("  No entities with null or phantom prototypes found.");
            return;
        }

        progress?.Report($"  Found {nullProtoObjects.Count} object(s) with null/phantom prototype — stripping…");
        var (removed, types) = StripSpecificObjects(resolver, nullProtoObjects, progress);
        progress?.Report($"  Stripped {removed} null-proto object(s).");
        foreach (var tn in types.Distinct().Take(20))
            progress?.Report($"    - {tn}");
    }

    /// <summary>
    /// Removes all entries in <paramref name="toRemove"/> from a Mafi Lyst-family object by
    /// compacting its backing array in-place. Used as a reliable fallback when the Lyst type
    /// has no public <c>Remove</c> method (e.g. <c>LystMutableDuringIter</c>).
    /// Returns the number of entries actually compacted out.
    /// </summary>
    private static int RemoveFromLystBacking(
        object lyst, ISet<object> toRemove, IProgress<string>? progress = null)
    {
        if (lyst is null || toRemove.Count == 0) return 0;
        const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Try Remove() first — if it works, great.
        var removeMethod = lyst.GetType().GetMethods(allInst)
            .FirstOrDefault(m => m.Name == "Remove"
                && m.GetParameters().Length == 1
                && !m.GetParameters()[0].ParameterType.IsValueType
                && !m.IsGenericMethodDefinition);
        if (removeMethod is not null)
        {
            int removed = 0;
            foreach (var item in toRemove)
            {
                try
                {
                    var result = removeMethod.Invoke(lyst, new[] { item });
                    // Treat bool return = true OR void return as success.
                    if (result is not bool b || b) removed++;
                }
                catch { }
            }
            if (removed == toRemove.Count) return removed;
            // If Remove() didn't get everything, fall through to backing-array compaction.
        }

        // Backing-array compaction: find m_items (Array) + m_size (int) fields.
        FieldInfo? fiItems = null;
        FieldInfo? fiSize  = null;
        for (var cur = lyst.GetType(); cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var fi in cur.GetFields(allInst | BindingFlags.DeclaredOnly))
            {
                if (fiItems is null && fi.FieldType.IsArray) fiItems = fi;
                if (fiSize  is null && fi.FieldType == typeof(int)
                    && (fi.Name == "m_size" || fi.Name == "_size" || fi.Name.Contains("size") || fi.Name.Contains("Size")))
                    fiSize = fi;
            }
            if (fiItems is not null && fiSize is not null) break;
        }

        if (fiItems is null || fiSize is null)
        {
            progress?.Report($"    [RemoveFromLystBacking] No backing array found on {lyst.GetType().Name}; cannot compact.");
            return 0;
        }

        var arr  = fiItems.GetValue(lyst) as Array;
        int size = 0;
        try { size = (int)(fiSize.GetValue(lyst) ?? 0); } catch { }
        if (arr is null || size <= 0) return 0;

        int write = 0;
        int compacted = 0;
        for (int i = 0; i < size; i++)
        {
            var item = arr.GetValue(i);
            if (item is not null && toRemove.Contains(item))
            { compacted++; continue; }
            if (write != i) arr.SetValue(item, write);
            write++;
        }
        for (int i = write; i < size; i++) arr.SetValue(null, i);
        try { fiSize.SetValue(lyst, write); } catch { }
        return compacted;
    }

    /// <summary>
    /// Digs into EntitiesManager and strips entities whose prototype fields are null.
    /// </summary>
    private void StripNullProtoEntitiesFromManagers(object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        var resolverType = resolver.GetType();
        var fiResolved = FindFieldDeep(resolverType, "m_resolvedObjects");
        if (fiResolved is null) return;

        var resolved = fiResolved.GetValue(resolver) as System.Collections.IEnumerable;
        if (resolved is null) return;

        object? entitiesManager = null;
        var tEntitiesManager = AssemblyLoader.FindType("Mafi.Core.Entities.EntitiesManager");
        if (tEntitiesManager is null)
        {
            progress?.Report("  Could not locate EntitiesManager type — skipping entity collection cleanup.");
            return;
        }

        foreach (var obj in resolved)
        {
            if (obj is not null && tEntitiesManager.IsAssignableFrom(obj.GetType()))
            {
                entitiesManager = obj;
                break;
            }
        }

        if (entitiesManager is null)
        {
            progress?.Report("  EntitiesManager not found in resolved objects — skipping.");
            return;
        }

        var emType = entitiesManager.GetType();
        var fiLinear = FindFieldDeep(emType, "m_entitiesLinear");
        if (fiLinear is null) return;
        var entitiesLinear = fiLinear.GetValue(entitiesManager);
        if (entitiesLinear is null) return;

        var allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var tProto = AssemblyLoader.FindType("Mafi.Core.Prototypes.Proto");
        var nullProtoEntities = new List<object>();

        foreach (var entity in (System.Collections.IEnumerable)entitiesLinear)
        {
            if (entity is null) continue;
            bool hasNullProto = false;
            var entityType = entity.GetType();

            for (var cur = entityType; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                foreach (var fi in cur.GetFields(allFlags | BindingFlags.DeclaredOnly))
                {
                    if (fi.FieldType.IsValueType) continue;

                    // Check the primary proto field AND backing fields of Proto-typed auto-properties.
                    // The broad tProto.IsAssignableFrom check is intentionally excluded — it strips
                    // vanilla entities (Shipyard, CaptainOffice) that have phantom secondary non-backing
                    // proto fields (e.g. from upgrade chains added by a removed mod).
                    // Backing fields (e.g. <Prototype>k__BackingField) are included because some entity
                    // classes expose their primary proto via an auto-property whose backing field differs
                    // from m_proto — excluding them left trucks and BattleShip with phantom renderer
                    // protos in EntitiesManager, causing EntitiesRenderingManager.getEntityRendererIndex
                    // to NPE during render initialization.
                    bool isPrimaryProtoField = fi.Name.Equals("m_proto", StringComparison.Ordinal)
                        || fi.Name.Equals("m_prototype", StringComparison.OrdinalIgnoreCase)
                        || (fi.Name.StartsWith("<", StringComparison.Ordinal)
                            && fi.Name.EndsWith(">k__BackingField", StringComparison.Ordinal)
                            && fi.Name.IndexOf("Proto", 1, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (!isPrimaryProtoField) continue;

                    try
                    {
                        var protoVal = fi.GetValue(entity);
                        if (protoVal is null || (_phantomProtoStubs is not null && _phantomProtoStubs.Contains(protoVal)))
                        {
                            hasNullProto = true;
                            break;
                        }
                    }
                    catch { }
                }
                if (hasNullProto) break;
            }

            // If the primary proto is phantom, try to replace it with a vanilla equivalent
            // from the healing lookup before resorting to stripping.  A unique vanilla entity
            // (Shipyard, CaptainOffice …) whose proto was swapped by COIExtended has exactly
            // one vanilla proto of that concrete type in ProtosDb → single-candidate heal.
            // Only strip when healing is impossible (no lookup, null proto, or no candidate).
            if (hasNullProto)
            {
                if (!TryHealEntityPrimaryProto(entity, allFlags, progress))
                    nullProtoEntities.Add(entity);
            }
        }

        if (nullProtoEntities.Count == 0)
        {
            progress?.Report("  No null/phantom-proto entities found inside EntitiesManager.");
            return;
        }

        progress?.Report($"  Found {nullProtoEntities.Count} null/phantom-proto entity(ies) inside EntitiesManager — stripping…");

        var entitySet = new HashSet<object>(nullProtoEntities, ReferenceEqualityComparer.Instance);

        foreach (var entity in nullProtoEntities)
            progress?.Report($"    Stripping entity: {entity.GetType().FullName ?? entity.GetType().Name}");

        // Use backing-array compaction for m_entitiesLinear (LystMutableDuringIter may have no public Remove).
        int removedLinear = RemoveFromLystBacking(entitiesLinear, entitySet, progress);
        progress?.Report($"    Compacted m_entitiesLinear: {removedLinear} removed.");

        var fiSet      = FindFieldDeep(emType, "m_entities");
        var entitiesSet = fiSet?.GetValue(entitiesManager);
        if (entitiesSet is not null)
        {
            int removedSet = RemoveFromLystBacking(entitiesSet, entitySet, progress);
            progress?.Report($"    m_entities ({entitiesSet.GetType().Name}): {removedSet} removed of {entitySet.Count} requested.");
        }

        var fiSimUpdate    = FindFieldDeep(emType, "m_entitiesWithSimUpdate");
        var entitiesSimUpd = fiSimUpdate?.GetValue(entitiesManager);
        if (entitiesSimUpd is not null)
        {
            int removedSim = RemoveFromLystBacking(entitiesSimUpd, entitySet, progress);
            progress?.Report($"    m_entitiesWithSimUpdate ({entitiesSimUpd.GetType().Name}): {removedSim} removed of {entitySet.Count} requested.");
        }

        // m_entitiesById is keyed by EntityId, not entity ref — remove by ID.
        var fiById      = FindFieldDeep(emType, "m_entitiesById");
        var entitiesById = fiById?.GetValue(entitiesManager);
        if (entitiesById is not null)
        {
            const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var removeById = entitiesById.GetType().GetMethods(allInst)
                .FirstOrDefault(m => m.Name == "Remove" && m.GetParameters().Length == 1);
            if (removeById is not null)
            {
                foreach (var entity in nullProtoEntities)
                {
                    try
                    {
                        var idField = FindFieldDeep(entity.GetType(), "m_id");
                        var id = idField?.GetValue(entity);
                        if (id is not null) removeById.Invoke(entitiesById, new[] { id });
                    }
                    catch { }
                }
            }
        }

        StripSpecificObjects(resolver, entitySet, progress);

        progress?.Report($"  Stripped {nullProtoEntities.Count} null-proto entities from EntitiesManager.");
    }

    /// <summary>
    /// Returns true if <paramref name="root"/> (or any of its nested non-collection
    /// reference fields, up to <paramref name="maxDepth"/> levels) holds a phantom proto
    /// stub or a null reference in a field whose declared type name ends in "Proto".
    /// <para/>
    /// Used by <see cref="StripBrokenTrajectoryEntities"/> to flag entities whose
    /// post-load init or renderer-init phase is guaranteed to NRE because their owned
    /// sub-objects (TransportTrajectory, IoPort, MaintenanceProvider, …) reference
    /// stripped/healed protos.
    /// <para/>
    /// Walks fields only on the receiver and its directly-referenced reference fields.
    /// Skips collections, strings, and value-type fields (other than nested classes).
    /// Records the first broken path it finds in <paramref name="brokenReason"/>.
    /// </summary>
    private bool HasBrokenProtoRef(object root, BindingFlags allFlags, int maxDepth, IProgress<string>? progressForHealing, ref string? brokenReason)
    {
        // Use a small explicit visited set to avoid cycles.
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance) { root };
        // Local function can't capture a `ref` parameter, so route the reason through
        // a single-element array.
        var reasonHolder = new string?[1];
        bool found = walk(root, "", 0);
        if (found) brokenReason = reasonHolder[0];
        return found;

        bool walk(object obj, string pathPrefix, int depth)
        {
            var t = obj.GetType();
            for (var cur = t; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo[] fields;
                try { fields = cur.GetFields(allFlags); }
                catch { continue; }

                foreach (var fi in fields)
                {
                    var ft = fi.FieldType;
                    if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) continue;

                    object? val;
                    try { val = fi.GetValue(obj); } catch { continue; }

                    var path = pathPrefix.Length == 0 ? fi.Name : pathPrefix + "." + fi.Name;

                    // (1) Field whose DECLARED TYPE is a single Proto reference → null /
                    //     phantom is a guaranteed render/init NRE. Excludes:
                    //       - Collections of protos (e.g. UnlockedProtosDb.m_unlockedProtos
                    //         which is a Set<T>) — those are normal manager state, not entity
                    //         breakage. We test that ft itself isn't a collection generic.
                    //       - Fields named "*proto" but typed as a primitive ID (ProtoId etc.).
                    //     The trigger is "declared field type is a class whose name ends in
                    //     'Proto'" (TransportProto, IoPortProto, ResearchNodeProto, …).
                    bool looksLikeProtoField =
                        !ft.IsValueType
                        && ft != typeof(string)
                        && ft.Name.EndsWith("Proto", StringComparison.Ordinal)
                        // Exclude proto-typed COLLECTIONS (e.g. ImmutableArray<TransportProto>,
                        // Lyst<TProto>, Set<TProto>). A collection of protos being null is
                        // expected for many managers and not a per-entity NRE risk.
                        && !typeof(System.Collections.IEnumerable).IsAssignableFrom(ft);

                    if (looksLikeProtoField)
                    {
                        if (val is null)
                        {
                            reasonHolder[0] = $"{path} ({ft.Name}) is null";
                            return true;
                        }
                        if (_phantomProtoStubs is not null && _phantomProtoStubs.Contains(val))
                        {
                            // CRITICAL: a phantom stub here does NOT necessarily mean the
                            // entity is broken. The proto-healing pass walks primary proto
                            // fields (m_proto / <Prototype>k__BackingField) and replaces
                            // phantoms with vanilla equivalents, but it doesn't touch every
                            // proto-typed field on every nested object. Before declaring the
                            // entity broken, try to heal THIS specific field with the same
                            // healing lookup. If the heal succeeds, the entity is fine and
                            // we should not strip it. If the heal fails (no vanilla candidate
                            // for this proto type), we have no choice but to strip — keeping
                            // it would NRE the renderer or game-logic init.
                            // Without this, we wrongly strip ~15K healthy BarrierEntity
                            // instances (and any other entity whose primary proto was healed
                            // earlier but a sibling Prototype-backing-field still references
                            // the original phantom stub object).
                            if (_protoHealingLookup is not null
                                && TryHealPhantomField(obj, fi, val, _protoHealingLookup, progressForHealing))
                            {
                                continue; // healed → not broken on this field
                            }
                            reasonHolder[0] = $"{path} ({ft.Name}) is phantom stub";
                            return true;
                        }
                    }

                    // (2) Pivot list: null or empty.
                    if (val is not null && fi.Name.Contains("ivot", StringComparison.Ordinal))
                    {
                        var vt = val.GetType();
                        var lenProp = vt.GetProperty("Length") ?? vt.GetProperty("Count");
                        if (lenProp is not null)
                        {
                            try
                            {
                                if (lenProp.GetValue(val) is int n && n == 0)
                                {
                                    reasonHolder[0] = $"{path} is empty";
                                    return true;
                                }
                            }
                            catch { }
                        }
                    }

                    // (3) Recurse into reference-typed fields whose value isn't null,
                    //     skipping collections (handled separately), strings, and value
                    //     types. Stop at maxDepth to keep the scan bounded.
                    //
                    //     CRITICAL: do NOT recurse into singleton manager / db objects.
                    //     Their fields (ProtosDb, m_unlockedProtosDb, m_statsManager, …)
                    //     are shared resolver state and will trip false positives on every
                    //     entity that happens to hold a back-reference to them. Stop the
                    //     descent at any object whose type name suggests it's a singleton
                    //     (Manager, Db, Database, Service, Provider, Registry).
                    if (val is null) continue;
                    if (depth + 1 > maxDepth) continue;

                    var valType = val.GetType();
                    if (valType.IsValueType) continue;
                    if (val is System.Collections.IEnumerable && val is not string) continue;

                    if (LooksLikeSingleton(valType)) continue;

                    if (visited.Add(val))
                    {
                        if (walk(val, path, depth + 1)) return true;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Returns true if <paramref name="t"/> looks like a shared-singleton object whose
    /// fields should NOT be probed for per-entity breakage. Used by
    /// <see cref="HasBrokenProtoRef"/> to stop graph-walking at manager / db /
    /// service boundaries that hold normal-but-nullable internal state.
    /// </summary>
    private static bool LooksLikeSingleton(Type t)
    {
        var n = t.Name;
        return n.EndsWith("Manager", StringComparison.Ordinal)
            || n.EndsWith("Db", StringComparison.Ordinal)
            || n.EndsWith("Database", StringComparison.Ordinal)
            || n.EndsWith("Service", StringComparison.Ordinal)
            || n.EndsWith("Provider", StringComparison.Ordinal)
            || n.EndsWith("Registry", StringComparison.Ordinal)
            || n.EndsWith("Repository", StringComparison.Ordinal)
            || n.EndsWith("Factory", StringComparison.Ordinal)
            || n.EndsWith("Controller", StringComparison.Ordinal)
            || n.EndsWith("Cache", StringComparison.Ordinal)
            || n.EndsWith("Pool", StringComparison.Ordinal);
    }

    /// <summary>
    /// Strips entities whose internal post-load state is unusable because a removed-mod
    /// system was responsible for setting it up. Specifically targets entities that own
    /// a <c>TransportTrajectory</c> field whose value is null OR whose <c>m_pivots</c>
    /// inner field is null/empty — these crash <c>Transport.initAfterLoad</c> with NRE
    /// inside <c>TransportTrajectory.tryCreateCurveFromPivots</c>.
    /// <para/>
    /// Concrete failure mode (Promised Frontier deep6):
    /// <code>
    /// NullReferenceException
    ///   at TransportTrajectory.tryCreateCurveFromPivots(TransportProto, ImmutableArray pivots, …)
    ///   at TransportTrajectory.recomputeCurveOrThrow(…)
    ///   at Transport.updatePowerConsumption()
    ///   at Transport.initAfterLoad(saveVersion)
    /// </code>
    /// then in the next phase:
    /// <code>
    /// NullReferenceException
    ///   at TransportHelper.ComputeOccupiedTilesRelative(origin, trajectory, terrainManager)
    ///   at Transport.get_OccupiedTiles()
    ///   at OceanEntitiesManager.areAllTilesOnOcean(IStaticEntity)
    ///   at OceanEntitiesManager.rebuildFromExistingEntities()
    /// </code>
    /// <para/>
    /// These Transports/Lifts/Conveyors had their primary proto healed to a vanilla
    /// equivalent during <see cref="StripNullProtoEntitiesFromManagers"/>, so they
    /// survive the null-proto sweep — but the mod-side initialiser that would have
    /// populated their trajectory pivots is gone, leaving them in a state the vanilla
    /// game cannot recover from.
    /// <para/>
    /// We strip these entities entirely. Without them present, <c>OceanEntitiesManager</c>
    /// has nothing to inspect and game initialization completes.
    /// </summary>
    private void StripBrokenTrajectoryEntities(
        object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var resolverType = resolver.GetType();
        var fiResolved = FindFieldDeep(resolverType, "m_resolvedObjects");
        if (fiResolved is null) return;
        if (fiResolved.GetValue(resolver) is not System.Collections.IEnumerable resolved) return;

        var tEntitiesManager = AssemblyLoader.FindType("Mafi.Core.Entities.EntitiesManager");
        if (tEntitiesManager is null)
        {
            progress?.Report("  Broken-trajectory scrub: EntitiesManager type not loaded; skipping.");
            return;
        }

        object? entitiesManager = null;
        foreach (var obj in resolved)
        {
            if (obj is not null && tEntitiesManager.IsAssignableFrom(obj.GetType()))
            {
                entitiesManager = obj;
                break;
            }
        }
        if (entitiesManager is null) return;

        var emType = entitiesManager.GetType();
        var fiLinear = FindFieldDeep(emType, "m_entitiesLinear");
        var entitiesLinear = fiLinear?.GetValue(entitiesManager) as System.Collections.IEnumerable;
        if (entitiesLinear is null) return;

        // Walk every entity. An entity is "broken" if it (or any of its directly-owned
        // sub-objects up to depth 3) holds a phantom proto stub or a null in a field
        // whose declared type is a Proto subclass. Common breakage shapes covered:
        //   - Transport.m_trajectory.m_proto (TransportProto) is phantom stub  → trajectory.recomputeCurveOrThrow NRE
        //   - Lift / MiniZipper IoPorts whose m_proto / m_connected.m_proto is phantom → IoPort.GetMaxThroughputPerTick NRE
        //   - Machine / EntityMaintenanceProvider with a phantom-stub maintenance provider → initSelf NRE
        // These entities crash either at initAfterLoad (logged but non-fatal) OR at the
        // renderer-init phase (EntitiesRenderingManager.getEntityRendererIndex returns
        // null because the proto has no renderer registration), which IS fatal.
        var brokenEntities = new List<object>();
        var byTypeCount = new Dictionary<string, int>(StringComparer.Ordinal);
        int scanned = 0;
        const int maxDepth = 3;

        foreach (var entity in entitiesLinear)
        {
            if (entity is null) continue;
            scanned++;
            var et = entity.GetType();
            string? brokenReason = null;
            if (HasBrokenProtoRef(entity, allFlags, maxDepth, progress, ref brokenReason))
            {
                brokenEntities.Add(entity);
                var key = (et.FullName ?? et.Name) + "  |  " + (brokenReason ?? "?");
                byTypeCount[key] = byTypeCount.GetValueOrDefault(key) + 1;
            }
        }

        if (brokenEntities.Count == 0)
        {
            progress?.Report($"  Broken-trajectory scrub: no entities with null/empty trajectory found ({scanned} scanned).");
            return;
        }

        progress?.Report($"  Broken-trajectory scrub: found {brokenEntities.Count} entity(ies) with unusable trajectory state ({scanned} scanned). By type:");
        foreach (var (k, v) in byTypeCount.OrderByDescending(kv => kv.Value).Take(15))
            progress?.Report($"    × {v,5}  {k}");

        var entitySet = new HashSet<object>(brokenEntities, ReferenceEqualityComparer.Instance);

        // Stash for the IoPort scrub: any IoPort whose OwnerEntity is one of these
        // entities must also be removed from IoPortsManager.m_ports, otherwise the
        // renderer's IoPortsRenderer.initState iterates ports owned by stripped
        // entities and NREs in InstancedChunkBasedLayoutEntitiesRenderer.GetBlueprintColor.
        if (_strippedBrokenEntities is null)
            _strippedBrokenEntities = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var e in brokenEntities) _strippedBrokenEntities.Add(e);

        // CRITICAL: StripSpecificObjects only touches resolver-level singleton collections
        // (m_resolvedObjects, m_resolvedInstancesByRealType, …). Entities live in
        // EntitiesManager's own collections (m_entitiesLinear, m_entities, m_entitiesById,
        // m_entitiesWithSimUpdate), so we must remove from those directly using the same
        // backing-array compaction pattern as StripNullProtoEntitiesFromManagers.
        // Without this, the strip is a no-op for entity-typed objects: the broken Transports
        // remain in the EntitiesManager and crash OceanEntitiesManager.rebuildFromExistingEntities
        // on game-load.
        int removedLinear = RemoveFromLystBacking(entitiesLinear, entitySet, progress);
        progress?.Report($"    [traj-scrub] m_entitiesLinear: {removedLinear} removed.");

        var fiSetBT = FindFieldDeep(emType, "m_entities");
        var entitiesSetBT = fiSetBT?.GetValue(entitiesManager);
        if (entitiesSetBT is not null)
        {
            int removedSet = RemoveFromLystBacking(entitiesSetBT, entitySet, progress);
            progress?.Report($"    [traj-scrub] m_entities ({entitiesSetBT.GetType().Name}): {removedSet} removed of {entitySet.Count} requested.");
        }

        var fiSimBT = FindFieldDeep(emType, "m_entitiesWithSimUpdate");
        var entitiesSimBT = fiSimBT?.GetValue(entitiesManager);
        if (entitiesSimBT is not null)
        {
            int removedSim = RemoveFromLystBacking(entitiesSimBT, entitySet, progress);
            progress?.Report($"    [traj-scrub] m_entitiesWithSimUpdate ({entitiesSimBT.GetType().Name}): {removedSim} removed of {entitySet.Count} requested.");
        }

        // m_entitiesById is keyed by EntityId; remove by ID via reflection.
        var fiByIdBT = FindFieldDeep(emType, "m_entitiesById");
        var entitiesByIdBT = fiByIdBT?.GetValue(entitiesManager);
        if (entitiesByIdBT is not null)
        {
            const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var removeByIdBT = entitiesByIdBT.GetType().GetMethods(allInst)
                .FirstOrDefault(m => m.Name == "Remove" && m.GetParameters().Length == 1);
            if (removeByIdBT is not null)
            {
                int removedById = 0;
                foreach (var entity in brokenEntities)
                {
                    try
                    {
                        var idField = FindFieldDeep(entity.GetType(), "m_id");
                        if (idField is null) continue;
                        var idVal = idField.GetValue(entity);
                        if (idVal is null) continue;
                        var paramType = removeByIdBT.GetParameters()[0].ParameterType;
                        if (!paramType.IsAssignableFrom(idVal.GetType())) continue;
                        var result = removeByIdBT.Invoke(entitiesByIdBT, new[] { idVal });
                        if (result is not bool b || b) removedById++;
                    }
                    catch { }
                }
                progress?.Report($"    [traj-scrub] m_entitiesById: {removedById} removed by ID.");
            }
        }

        // Also call StripSpecificObjects for any cases where the entity also happens
        // to be in resolver-level collections (defence in depth).
        StripSpecificObjects(resolver, entitySet, progress);

        progress?.Report($"  Broken-trajectory scrub: stripped {brokenEntities.Count} entity(ies) from EntitiesManager.");
    }

    /// <summary>
    /// Second entity-stripping pass: removes entities whose concrete runtime type belongs
    /// to a removed-mod assembly, regardless of whether their proto was healed. Catches
    /// e.g. SmartZipper (healed to a vanilla Zipper proto but still of type
    /// COIExtended.Automation.Entities.SmartZipper.SmartZipper) that would otherwise
    /// survive into the serialized output and cause AQN validator failures.
    /// Must run after <see cref="StripNullProtoEntitiesFromManagers"/> so that proto-healing
    /// has already run and we only encounter entities that genuinely have mod-assembly types.
    /// </summary>
    private void StripRemovedModTypeEntitiesFromManagers(
        object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        if (stripAssemblies.Count == 0) return;

        var resolverType = resolver.GetType();
        var fiResolved   = FindFieldDeep(resolverType, "m_resolvedObjects");
        var resolved     = fiResolved?.GetValue(resolver) as System.Collections.IEnumerable;
        if (resolved is null) return;

        var tEntitiesManager = AssemblyLoader.FindType("Mafi.Core.Entities.EntitiesManager");
        if (tEntitiesManager is null) return;

        object? entitiesManager = null;
        foreach (var obj in resolved)
        {
            if (obj is not null && tEntitiesManager.IsAssignableFrom(obj.GetType()))
            { entitiesManager = obj; break; }
        }
        if (entitiesManager is null) return;

        var emType   = entitiesManager.GetType();
        var fiLinear = FindFieldDeep(emType, "m_entitiesLinear");
        if (fiLinear is null) return;
        var entitiesLinear = fiLinear.GetValue(entitiesManager);
        if (entitiesLinear is null) return;

        var modTypeEntities = new List<object>();
        foreach (var entity in (System.Collections.IEnumerable)entitiesLinear)
        {
            if (entity is null) continue;
            if (ShouldStrip(entity.GetType(), stripAssemblies))
                modTypeEntities.Add(entity);
        }

        if (modTypeEntities.Count == 0)
        {
            progress?.Report("  No removed-mod-type entities found in EntitiesManager.");
            return;
        }

        progress?.Report($"  Found {modTypeEntities.Count} removed-mod-type entity(ies) in EntitiesManager — stripping…");

        foreach (var entity in modTypeEntities)
            progress?.Report($"    Stripping mod-type entity: {entity.GetType().FullName}");

        var strippedSet = new HashSet<object>(modTypeEntities, ReferenceEqualityComparer.Instance);

        // Use backing-array compaction — LystMutableDuringIter may have no public Remove.
        int removedLinear = RemoveFromLystBacking(entitiesLinear, strippedSet, progress);
        progress?.Report($"    Compacted m_entitiesLinear: {removedLinear} removed.");

        var fiSet2     = FindFieldDeep(emType, "m_entities");
        var entitiesSet2 = fiSet2?.GetValue(entitiesManager);
        if (entitiesSet2 is not null)
        {
            int removedSet2 = RemoveFromLystBacking(entitiesSet2, strippedSet, progress);
            progress?.Report($"    m_entities ({entitiesSet2.GetType().Name}): {removedSet2} removed of {strippedSet.Count} requested.");
        }

        var fiSimUpdate2   = FindFieldDeep(emType, "m_entitiesWithSimUpdate");
        var entitiesSimUpd2 = fiSimUpdate2?.GetValue(entitiesManager);
        if (entitiesSimUpd2 is not null)
        {
            int removedSim2 = RemoveFromLystBacking(entitiesSimUpd2, strippedSet, progress);
            progress?.Report($"    m_entitiesWithSimUpdate ({entitiesSimUpd2.GetType().Name}): {removedSim2} removed of {strippedSet.Count} requested.");
        }

        var fiById2      = FindFieldDeep(emType, "m_entitiesById");
        var entitiesById2 = fiById2?.GetValue(entitiesManager);
        if (entitiesById2 is not null)
        {
            const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var removeById2 = entitiesById2.GetType().GetMethods(allInst)
                .FirstOrDefault(m => m.Name == "Remove" && m.GetParameters().Length == 1);
            if (removeById2 is not null)
            {
                foreach (var entity in modTypeEntities)
                {
                    try
                    {
                        var idField = FindFieldDeep(entity.GetType(), "m_id");
                        var id = idField?.GetValue(entity);
                        if (id is not null) removeById2.Invoke(entitiesById2, new[] { id });
                    }
                    catch { }
                }
            }
        }

        StripSpecificObjects(resolver, strippedSet, progress);

        progress?.Report($"  Stripped {modTypeEntities.Count} removed-mod-type entity(ies) from EntitiesManager.");

        // ── Diagnostic: enumerate EVERY IEnumerable field on EntitiesManager and ──
        // report whether any still contain a stripped entity. Catches side-collections
        // beyond the well-known m_entitiesLinear / m_entities / m_entitiesWithSimUpdate.
        ReportEntitiesManagerCollectionsHoldingStripped(entitiesManager, strippedSet, progress);

        // ── Diagnostic + targeted fix: find any remaining holder of the stripped ──
        // entities and null those reference fields. After all the structural removal
        // above, BFS the resolver graph and (a) log each holder for traceability and
        // (b) null its reference field so the BlobWriter can't emit the stripped
        // entity inline as part of the holder's serialised payload.
        //
        // This is the safety net behind every named-collection scrub above. It catches
        // edge cases like ElectricityConsumer.<Entity>k__BackingField holding a
        // ref to a stripped SmartZipper entity that survives because consumers live
        // behind a Lyst<ElectricityConsumer> the structural BFS doesn't recurse into.
        ScrubRemainingHoldersOfStrippedEntities(resolver, strippedSet, progress);
    }

    /// <summary>
    /// Walks the entire resolver-reachable object graph, finds every reference field
    /// (and every collection element) that holds a stripped entity, logs the holder
    /// site for traceability, AND nulls the field so the BlobWriter can't follow it
    /// during re-serialisation.
    /// <para/>
    /// Pairs diagnostic logging with the actual fix in one BFS pass — keeps the
    /// "where does the surviving ref live?" answer and the scrub aligned in code,
    /// so future regressions surface at the same call site.
    /// </summary>
    private static void ScrubRemainingHoldersOfStrippedEntities(
        object resolver, HashSet<object> stripped, IProgress<string>? progress)
    {
        if (stripped.Count == 0) { progress?.Report("    [holder-scrub] no stripped entities to scan for."); return; }

        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var resolverType = resolver.GetType();

        // Seed the BFS from every object the resolver knows about + the resolver itself.
        var roots = new List<object>();
        var fiResolved = FindFieldDeep(resolverType, "m_resolvedObjects");
        if (fiResolved?.GetValue(resolver) is System.Collections.IEnumerable resolved)
        {
            foreach (var o in resolved) if (o is not null) roots.Add(o);
        }
        roots.Add(resolver);

        progress?.Report($"    [holder-scrub] BFS from {roots.Count} resolver root(s) looking for holders of {stripped.Count} stripped entity(ies)…");

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        foreach (var r in roots) if (visited.Add(r)) queue.Enqueue(r);

        int nulled = 0;
        int hitBudget = 50;
        int objectsScanned = 0;
        const int VisitBudget = 500_000;
        const int QueueSizeCap = 300_000;

        while (queue.Count > 0 && objectsScanned < VisitBudget)
        {
            var obj = queue.Dequeue();
            if (obj is null) continue;
            objectsScanned++;
            var objType = obj.GetType();

            // Inline IEnumerable scan: catches Lyst, Set, List, Dict.Values, arrays.
            // We don't try to NULL collection-membership hits here — collections may be
            // arrays, hash sets, dictionaries with no uniform "remove by ref" path. The
            // owning structural strip pass should handle membership; we just log so the
            // user can see if such a case still leaks past every named-collection scrub.
            if (obj is System.Collections.IEnumerable enumerable && obj is not string)
            {
                int idx = 0;
                System.Collections.IEnumerator? en = null;
                try { en = enumerable.GetEnumerator(); }
                catch { en = null; }
                if (en is not null)
                {
                    try
                    {
                        while (true)
                        {
                            bool moved;
                            try { moved = en.MoveNext(); }
                            catch { break; }
                            if (!moved) break;

                            object? item;
                            try { item = en.Current; }
                            catch { idx++; continue; }
                            if (item is null) { idx++; continue; }

                            if (stripped.Contains(item))
                            {
                                if (hitBudget-- > 0)
                                    progress?.Report($"    [holder-scrub] HIT (collection — NOT nulled, owner must drop): {objType.FullName}[{idx}] = {item.GetType().Name}");
                            }
                            else if (!item.GetType().IsValueType
                                     && !(item is string)
                                     && visited.Add(item)
                                     && queue.Count < QueueSizeCap)
                            {
                                queue.Enqueue(item);
                            }
                            idx++;
                            if (idx > 1_000_000) break;
                        }
                    }
                    finally
                    {
                        if (en is IDisposable disp) try { disp.Dispose(); } catch { }
                    }
                }
            }

            // Field walk: NULL any reference field whose value is in `stripped`.
            for (var cur = objType; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo[] fields;
                try { fields = cur.GetFields(allFlags); }
                catch { continue; }

                foreach (var fi in fields)
                {
                    var ft = fi.FieldType;
                    if (ft == typeof(string) || ft.IsPrimitive || ft.IsEnum) continue;

                    object? val;
                    try { val = fi.GetValue(obj); }
                    catch { continue; }
                    if (val is null) continue;

                    if (!ft.IsValueType)
                    {
                        if (stripped.Contains(val))
                        {
                            try
                            {
                                fi.SetValue(obj, null);
                                nulled++;
                                if (hitBudget-- > 0)
                                    progress?.Report($"    [holder-scrub] NULLED {objType.FullName}.{fi.Name} (was {val.GetType().Name})");
                            }
                            catch (Exception ex)
                            {
                                if (hitBudget-- > 0)
                                    progress?.Report($"    [holder-scrub] FAILED to null {objType.FullName}.{fi.Name}: {ex.GetType().Name}: {ex.Message}");
                            }
                            continue;
                        }
                        if (visited.Add(val) && queue.Count < QueueSizeCap) queue.Enqueue(val);
                    }
                }
            }
        }

        bool budgetHit = objectsScanned >= VisitBudget;
        progress?.Report($"    [holder-scrub] complete: {nulled} field(s) nulled across {objectsScanned:N0} object(s) scanned{(budgetHit ? " (visit budget hit)" : "")}.");
    }

    /// <summary>
    /// Diagnostic: enumerate every IEnumerable field on EntitiesManager (and its base
    /// types) and report any that still contain stripped entities. Helps identify
    /// side-collections beyond the well-known m_entitiesLinear / m_entities /
    /// m_entitiesWithSimUpdate / m_entitiesById that may still be holding refs to
    /// stripped entities. Does not modify anything; pure logging.
    /// </summary>
    private static void ReportEntitiesManagerCollectionsHoldingStripped(
        object entitiesManager, HashSet<object> stripped, IProgress<string>? progress)
    {
        if (stripped.Count == 0) return;

        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var emType = entitiesManager.GetType();
        int reported = 0;

        for (var cur = emType; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            FieldInfo[] fields;
            try { fields = cur.GetFields(allFlags); }
            catch { continue; }

            foreach (var fi in fields)
            {
                // Skip primitives and string fields — they can't hold entity refs.
                if (fi.FieldType.IsPrimitive || fi.FieldType.IsEnum || fi.FieldType == typeof(string)) continue;

                object? val;
                try { val = fi.GetValue(entitiesManager); }
                catch { continue; }
                if (val is null) continue;

                // Try as IEnumerable. Catches Lyst, Set, Dict (yields KVPs), array, List<T>, etc.
                if (val is not System.Collections.IEnumerable enumerable || val is string) continue;

                int hits = 0;
                int total = 0;
                System.Collections.IEnumerator? en = null;
                try { en = enumerable.GetEnumerator(); }
                catch { continue; }

                try
                {
                    while (true)
                    {
                        bool moved;
                        try { moved = en.MoveNext(); }
                        catch { break; }
                        if (!moved) break;

                        object? item;
                        try { item = en.Current; }
                        catch { total++; continue; }
                        total++;

                        if (item is null) continue;

                        // Direct reference match.
                        if (stripped.Contains(item)) { hits++; continue; }

                        // Dict<TKey, TValue> yields KeyValuePair: check both Key and Value.
                        var itemType = item.GetType();
                        if (itemType.IsGenericType && itemType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                        {
                            var kProp = itemType.GetProperty("Key");
                            var vProp = itemType.GetProperty("Value");
                            try
                            {
                                var k = kProp?.GetValue(item);
                                if (k is not null && stripped.Contains(k)) { hits++; continue; }
                            }
                            catch { }
                            try
                            {
                                var v = vProp?.GetValue(item);
                                if (v is not null && stripped.Contains(v)) { hits++; continue; }
                            }
                            catch { }
                        }

                        if (total > 5_000_000) break; // pathological guard
                    }
                }
                finally
                {
                    if (en is IDisposable disp) try { disp.Dispose(); } catch { }
                }

                if (hits > 0 && reported++ < 50)
                {
                    progress?.Report($"    [em-scan] EntitiesManager.{fi.Name} ({val.GetType().Name}) STILL HOLDS {hits}/{total} stripped entity(ies)");
                }
            }
        }

        if (reported == 0)
            progress?.Report("    [em-scan] EntitiesManager fields: no remaining holders of stripped entities.");
    }

    internal static bool ShouldStrip(Type? type, HashSet<string> stripAssemblies)
    {
        if (type is null) return false;
        string asmName = type.Assembly.GetName().Name ?? string.Empty;
        return stripAssemblies.Contains(asmName);
    }

    /// <summary>
    /// Scans every field of every resolver object and removes any phantom proto
    /// stubs found inside mutable collections (List, Set, etc.).
    /// This handles cases like ResearchManager's unlocked-node collections where
    /// phantom stubs end up stored by reference rather than by ID.
    /// Must be called BEFORE NullifyPhantomProtoIds so stubs are still identifiable.
    /// </summary>
    private void StripPhantomProtoRefsFromCollections(object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        if (_phantomProtoStubs is null || _phantomProtoStubs.Count == 0)
        {
            progress?.Report("  No phantom stubs — skipping collection phantom-ref cleanup.");
            return;
        }

        var fiResolved = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
        var resolved = fiResolved?.GetValue(resolver) as System.Collections.IEnumerable;
        if (resolved is null) return;

        // Also collect objects from the type-keyed dict — some managers may only live there.
        var allObjects = resolved.Cast<object>().ToList();
        var fiByType = FindFieldDeep(resolver.GetType(), "m_resolvedInstancesByRealType");
        if (fiByType?.GetValue(resolver) is System.Collections.IEnumerable byTypeDict)
        {
            foreach (var kv in byTypeDict.Cast<object>())
            {
                var valProp = kv?.GetType().GetProperty("Value");
                if (valProp?.GetValue(kv) is object v && !allObjects.Contains(v))
                    allObjects.Add(v);
            }
        }

        // Entities are intentionally NOT added to the BFS queue here.
        // Adding hundreds of thousands of entities causes the BFS to materialise
        // a ToList() for every collection field of every entity, flooding Gen0 GC
        // and causing OutOfMemoryException on large saves.
        // Instead, entities are scanned once afterwards via a streaming sweep that
        // allocates only when phantom refs are actually found (which is rare).
        var tEntitiesManager = AssemblyLoader.FindType("Mafi.Core.Entities.EntitiesManager");
        object? entitiesLinearForSweep = null;
        if (tEntitiesManager is not null)
        {
            var em = allObjects.FirstOrDefault(o => o is not null && tEntitiesManager.IsAssignableFrom(o.GetType()));
            if (em is not null)
            {
                var fiLinear = FindFieldDeep(em.GetType(), "m_entitiesLinear");
                entitiesLinearForSweep = fiLinear?.GetValue(em);
            }
        }

        var tProto = AssemblyLoader.FindType("Mafi.Core.Prototypes.Proto");
        var tIEntity = AssemblyLoader.FindType("Mafi.Core.Entities.IEntity");
        var allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        int totalRemoved = 0;
        int verboseReportBudget = 500; // cap individual removal lines to avoid log explosion on large saves
        int bfsVisits = 0;
        const int BfsVisitBudget  = 5_000_000;
        const int BfsQueueSizeCap = 2_000_000;

        // BFS: start with resolver objects, discover nested objects through collection fields.
        // This finds entities like LogisticsZone held in manager collections, not EntitiesManager.
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>(allObjects);
        foreach (var o in allObjects) visited.Add(o);

        while (queue.Count > 0 && bfsVisits < BfsVisitBudget)
        {
            var obj = queue.Dequeue();
            bfsVisits++;
            if (obj is null) continue;
            var objType = obj.GetType();

            for (var cur = objType; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                foreach (var fi in cur.GetFields(allFlags))
                {
                    bool isMafiImmArray = fi.FieldType.IsValueType
                        && fi.FieldType.IsGenericType
                        && fi.FieldType.Name.StartsWith("ImmutableArray", StringComparison.Ordinal);
                    bool isMafiOption = fi.FieldType.IsValueType
                        && fi.FieldType.IsGenericType
                        && fi.FieldType.Name.StartsWith("Option`", StringComparison.Ordinal);
                    bool isMafiLystStruct = fi.FieldType.IsValueType
                        && fi.FieldType.IsGenericType
                        && fi.FieldType.Name.StartsWith("LystStruct", StringComparison.Ordinal);
                    if (fi.FieldType.IsValueType && !isMafiImmArray && !isMafiOption && !isMafiLystStruct) continue;
                    if (fi.FieldType == typeof(string)) continue;

                    try
                    {
                        var val = fi.GetValue(obj);
                        if (val is null || val is string) continue;

                        // Direct proto reference field pointing to a phantom stub: null it out.
                        // This prevents re-serialization from writing phantom IDs that the game
                        // can't cast to the expected concrete Proto subtype (e.g. SpaceStationProto).
                        if (!fi.FieldType.IsValueType
                            && tProto is not null
                            && tProto.IsAssignableFrom(fi.FieldType)
                            && _phantomProtoStubs!.Contains(val))
                        {
                            fi.SetValue(obj, null);
                            totalRemoved++;
                            if (verboseReportBudget-- > 0) progress?.Report($"    Nulled phantom proto ref {objType.Name}.{fi.Name} ({val.GetType().Name})");
                            continue;
                        }

                        var colType = val.GetType();

                        // Handle Option<TProto>: nullify to None if inner value is a phantom stub.
                        // Scan backing fields directly — we don't know Mafi's Option<T> property names.
                        if (isMafiOption || (colType.IsValueType && colType.IsGenericType
                                && colType.Name.StartsWith("Option`", StringComparison.Ordinal)))
                        {
                            var innerType = colType.GetGenericArguments()[0];
                            if (!innerType.IsValueType)
                            {
                                object? inner = null;
                                var optFields = colType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                foreach (var optFi in optFields)
                                {
                                    if (optFi.FieldType.IsValueType) continue;
                                    if (!innerType.IsAssignableFrom(optFi.FieldType)) continue;
                                    try { inner = optFi.GetValue(val); break; }
                                    catch { }
                                }

                                bool clear = false;
                                string? reason = null;

                                // Existing rule: inner is a phantom proto stub.
                                if (inner is not null && _phantomProtoStubs!.Contains(inner))
                                { clear = true; reason = "phantom"; }

                                // New rule A: inner instance's runtime type comes from a removed-mod assembly.
                                // Re-serialisation would emit the original COIExtended type string and crash on load.
                                if (!clear && inner is not null && ShouldStrip(inner.GetType(), stripAssemblies))
                                { clear = true; reason = $"inner type {inner.GetType().FullName} from removed mod"; }

                                // New rule B: declared generic arg itself is from a removed mod (handles Option<T>
                                // whose dynamic-dispatch read writes T's type string even when inner is null/empty
                                // — e.g. AssetTransactionManager+GlobalBuffer.Option<COIExtended...FishFarm>).
                                if (!clear && ShouldStrip(innerType, stripAssemblies))
                                { clear = true; reason = $"generic arg {innerType.FullName} from removed mod"; }

                                if (clear)
                                {
                                    var noneField = colType.GetField("None", BindingFlags.Public | BindingFlags.Static);
                                    if (noneField is not null)
                                    {
                                        fi.SetValue(obj, noneField.GetValue(null));
                                        totalRemoved++;
                                        if (verboseReportBudget-- > 0) progress?.Report($"    Cleared Option<{innerType.Name}> ({reason}) in {objType.Name}.{fi.Name}");
                                    }
                                }
                            }
                            continue;
                        }

                        // LystStruct<T> AND Lyst<TStruct>: Mafi list whose items are value types,
                        // accessed via GetBackingArray() so we can write modified structs back
                        // (boxed struct mutations are not reflected in the original value).
                        // Without this, value-type items on a regular Lyst<TStruct> are skipped
                        // by the standard collection branch (which drops IsValueType items),
                        // leaving e.g. AssetTransactionManager.m_globalBuffers entries with
                        // intact Option<COIExtended…FishFarm> fields that crash the game on load.
                        bool isLystOfStruct = false;
                        if (!isMafiLystStruct
                            && colType.IsGenericType
                            && colType.Name.StartsWith("Lyst`", StringComparison.Ordinal))
                        {
                            var lystElem = colType.GetGenericArguments()[0];
                            isLystOfStruct = lystElem.IsValueType;
                        }

                        if (isMafiLystStruct || isLystOfStruct)
                        {
                            var lystType = val.GetType();
                            var lystCountProp = lystType.GetProperty("Count");
                            // Use the public GetBackingArray() method — more reliable than private field name lookup
                            // on closed generic types. Returns m_items ?? Array.Empty<T>().
                            var getBackingArrayMi = lystType.GetMethod("GetBackingArray", BindingFlags.Public | BindingFlags.Instance);
                            var lystBackingArr = getBackingArrayMi?.Invoke(val, null) as System.Array;
                            int lystCount = lystBackingArr is not null ? (int)(lystCountProp?.GetValue(val) ?? 0) : 0;

                            // Collect indices of struct items that reference a removed-mod type
                            // (e.g. CallbackSaveData with DeclaringType=DrydockManager). These
                            // must be removed via Lyst.RemoveAt so the dynamic-dispatch deserializer
                            // doesn't attempt to load the missing type from the re-serialised save.
                            var indicesToRemoveStruct = new List<int>();

                            for (int lystIdx = 0; lystIdx < lystCount; lystIdx++)
                            {
                                var lystItem = lystBackingArr!.GetValue(lystIdx);
                                if (lystItem is null) continue;
                                var liType = lystItem.GetType();
                                if (!liType.IsValueType)
                                {
                                    if (visited.Add(lystItem) && queue.Count < BfsQueueSizeCap) queue.Enqueue(lystItem);
                                    continue;
                                }

                                // Struct item refers to a removed-mod type → mark for removal.
                                // ItemRefersToRemovedMod walks reference fields including Type
                                // refs (CallbackSaveData.DeclaringType) and instance refs.
                                if (ItemRefersToRemovedMod(lystItem, stripAssemblies, allFlags))
                                {
                                    indicesToRemoveStruct.Add(lystIdx);
                                    if (verboseReportBudget-- > 0)
                                        progress?.Report($"    Marking struct {liType.Name} at {objType.Name}.{fi.Name}[{lystIdx}] for removal (refers to removed-mod type)");
                                    continue;
                                }

                                bool lystItemModified = false;
                                // Struct item: scan its reference-type fields for direct phantom proto
                                // refs and phantom proto collections.
                                for (var liCur = liType; liCur is not null && liCur != typeof(object); liCur = liCur.BaseType)
                                {
                                    foreach (var liFi in liCur.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                                    {
                                        // Option<T> value-type field on a struct item: clear when T comes from
                                        // a removed mod assembly. Re-serialisation otherwise writes T's full
                                        // type string and the game crashes with CorruptedSaveException
                                        // (e.g. AssetTransactionManager+GlobalBuffer.Option<COIExtended…FishFarm>).
                                        if (liFi.FieldType.IsValueType
                                            && liFi.FieldType.IsGenericType
                                            && liFi.FieldType.Name.StartsWith("Option`", StringComparison.Ordinal))
                                        {
                                            var liInnerType = liFi.FieldType.GetGenericArguments()[0];
                                            if (!liInnerType.IsValueType && ShouldStrip(liInnerType, stripAssemblies))
                                            {
                                                var liNoneField = liFi.FieldType.GetField("None", BindingFlags.Public | BindingFlags.Static);
                                                if (liNoneField is not null)
                                                {
                                                    try
                                                    {
                                                        liFi.SetValue(lystItem, liNoneField.GetValue(null));
                                                        lystItemModified = true;
                                                        totalRemoved++;
                                                        if (verboseReportBudget-- > 0)
                                                            progress?.Report($"    Cleared Option<{liInnerType.Name}> in struct {liType.Name} of {objType.Name}.{fi.Name}[{lystIdx}].{liFi.Name}");
                                                    }
                                                    catch { }
                                                }
                                            }
                                            continue;
                                        }

                                        if (liFi.FieldType.IsValueType) continue;
                                        if (liFi.FieldType == typeof(string)) continue;
                                        try
                                        {
                                            var liVal = liFi.GetValue(lystItem);
                                            if (liVal is null) continue;

                                            // Direct phantom proto reference in struct item: null it and
                                            // write the modified struct back to the backing array so the
                                            // reserialized save doesn't contain phantom proto IDs.
                                            // Do NOT guard with tProto.IsAssignableFrom(liFi.FieldType) —
                                            // the field may be declared as IEntityProto (an interface not
                                            // derived from Proto), which makes IsAssignableFrom return false
                                            // even though the actual value is a phantom proto stub.
                                            if (_phantomProtoStubs!.Contains(liVal))
                                            {
                                                liFi.SetValue(lystItem, null);
                                                lystItemModified = true;
                                                totalRemoved++;
                                                if (verboseReportBudget-- > 0)
                                                    progress?.Report($"    Nulled phantom {liVal.GetType().Name} in {objType.Name}.{fi.Name}[{lystIdx}].{liFi.Name}");
                                                continue;
                                            }

                                            if (liVal is System.Collections.IEnumerable liEnum)
                                            {
                                                var liValType = liVal.GetType();
                                                var typeArgs = liValType.IsGenericType
                                                    ? liValType.GetGenericArguments()
                                                    : Array.Empty<Type>();

                                                bool keyIsProto = typeArgs.Length >= 1
                                                    && !typeArgs[0].IsValueType
                                                    && tProto is not null
                                                    && tProto.IsAssignableFrom(typeArgs[0]);

                                                if (keyIsProto)
                                                {
                                                    if (typeArgs.Length >= 2)
                                                    {
                                                        // Dict<TProto, V>: remove phantom-keyed entries via Remove(key).
                                                        var liRemoveKey = liValType.GetMethod("Remove", new Type[] { typeArgs[0] });
                                                        if (liRemoveKey is not null)
                                                        {
                                                            var phantomKeys = new List<object>();
                                                            foreach (var kv in liEnum.Cast<object>())
                                                            {
                                                                if (kv is null) continue;
                                                                var k = kv.GetType().GetProperty("Key")?.GetValue(kv);
                                                                if (k is not null && _phantomProtoStubs!.Contains(k))
                                                                    phantomKeys.Add(k);
                                                            }
                                                            foreach (var pk in phantomKeys)
                                                            {
                                                                try { liRemoveKey.Invoke(liVal, new[] { pk }); totalRemoved++; }
                                                                catch { }
                                                            }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        // Set<TProto>: remove phantom items directly.
                                                        var liRemove = liValType.GetMethod("Remove", new Type[] { typeArgs[0] })
                                                            ?? liValType.GetMethod("Remove", new Type[] { typeof(object) });
                                                        if (liRemove is not null)
                                                        {
                                                            var phantomItems = liEnum.Cast<object>()
                                                                .Where(x => x is not null && _phantomProtoStubs!.Contains(x))
                                                                .ToList();
                                                            foreach (var pi in phantomItems)
                                                            {
                                                                try
                                                                {
                                                                    liRemove.Invoke(liVal, new[] { pi });
                                                                    totalRemoved++;
                                                                    if (verboseReportBudget-- > 0)
                                                                        progress?.Report($"    Removed {pi.GetType().Name} phantom from {objType.Name}.{fi.Name}.{liFi.Name}");
                                                                }
                                                                catch { }
                                                            }
                                                        }
                                                    }
                                                }
                                            }

                                            // Add the ref-type field value to BFS for deeper traversal.
                                            if (visited.Add(liVal) && queue.Count < BfsQueueSizeCap) queue.Enqueue(liVal);
                                        }
                                        catch { }
                                    }
                                }

                                if (lystItemModified)
                                {
                                    // Write the modified struct back into the backing array.
                                    // This is necessary because SetValue on a boxed struct modifies
                                    // the box, not the original — so we must copy it back explicitly.
                                    try { lystBackingArr!.SetValue(lystItem, lystIdx); }
                                    catch { }
                                }
                            }

                            // Remove flagged struct items via Lyst.RemoveAt(int) in reverse
                            // index order so earlier indices stay valid as the list shrinks.
                            if (indicesToRemoveStruct.Count > 0)
                            {
                                var lystRemoveAt = lystType.GetMethod("RemoveAt", new[] { typeof(int) });
                                if (lystRemoveAt is not null)
                                {
                                    for (int k = indicesToRemoveStruct.Count - 1; k >= 0; k--)
                                    {
                                        try
                                        {
                                            lystRemoveAt.Invoke(val, new object[] { indicesToRemoveStruct[k] });
                                            totalRemoved++;
                                        }
                                        catch { }
                                    }
                                    if (verboseReportBudget-- > 0)
                                        progress?.Report($"    Removed {indicesToRemoveStruct.Count} struct item(s) referring to removed-mod types from {objType.Name}.{fi.Name}");
                                }
                            }
                            continue;
                        }

                        // Enumerate the collection (ImmutableArray or any other).
                        // For ImmutableArray we must enumerate before the IEnumerable check
                        // since Mafi's struct may not implement non-generic IEnumerable.
                        bool isImmArr = isMafiImmArray || (colType.IsValueType && colType.IsGenericType
                                && colType.Name.StartsWith("ImmutableArray", StringComparison.Ordinal));

                        List<object> colItems;
                        if (isImmArr)
                            colItems = EnumerateMafiImmutableArray(val);
                        else if (val is System.Collections.IEnumerable col)
                            colItems = col.Cast<object>().ToList();
                        else
                        {
                            colItems = EnumerateMafiImmutableArray(val); // GetEnumerator fallback
                            // Not a collection (e.g. EventBase<T>): enqueue the object itself for
                            // BFS so its own fields (like m_callbacksSaveData) get processed.
                            if (colItems.Count == 0
                                && !colType.IsValueType
                                && colType != typeof(string)
                                && (tProto is null || !tProto.IsAssignableFrom(colType))
                                && visited.Add(val)
                                && queue.Count < BfsQueueSizeCap)
                            {
                                queue.Enqueue(val);
                            }
                        }

                        // BFS expansion: enqueue non-proto class items from manager/resolver
                        // collections so we reach nested objects (e.g. LogisticsZone inside a
                        // manager list).  Entities are excluded: all entities are already added
                        // to allObjects directly, and following entity→entity connections
                        // (e.g. conveyor belt chains) would make the BFS grow to millions of
                        // items on a large save — causing an apparent hang.
                        bool objIsEntity = tIEntity is not null && tIEntity.IsAssignableFrom(objType);
                        if (!objIsEntity && tProto is not null)
                        {
                            foreach (var item in colItems)
                            {
                                if (item is null) continue;
                                var iType = item.GetType();
                                if (iType.IsValueType) continue;
                                if (iType == typeof(string)) continue;
                                if (tProto.IsAssignableFrom(iType)) continue; // proto — not a container
                                if (visited.Add(item) && queue.Count < BfsQueueSizeCap) queue.Enqueue(item);
                            }
                        }

                        if (isImmArr)
                        {
                            // SlimIdManagers use ManagedProtos as a compact index array — every entity
                            // in the save that stores a slim ID depends on stable indices here.
                            // Removing a phantom would shift all subsequent indices, corrupting tile/entity data.
                            // Leave these alone; the game's own initAfterLoad handles unknown IDs gracefully.
                            if (fi.Name == "ManagedProtos" && objType.Name.Contains("SlimIdManager"))
                                continue;

                            // ProductsManager.ProductStats is indexed by slim ID (same index as ManagedProtos).
                            // Removing phantom entries would shift indices and cause "Products changed after load"
                            // errors when the game's initAfterLoad compares ProductStats[i] with ManagedProtos[i].
                            // ProductStats is an auto-property; its IL backing field is "<ProductStats>k__BackingField".
                            bool isProductStatsField = fi.Name == "ProductStats"
                                || fi.Name == "<ProductStats>k__BackingField";
                            if (isProductStatsField && objType.Name == "ProductsManager")
                                continue;

                            // ImmutableArray: rebuild without phantom items.
                            // IsVanillaType is intentionally NOT used here: phantom stubs of
                            // vanilla proto types (e.g. DiseaseProto) must still be stripped,
                            // otherwise the game's BlobReader throws a failed-cast exception.
                            var elemType = colType.GetGenericArguments()[0];
                            if (!elemType.IsValueType)
                            {
                                var kept = colItems
                                    .Where(item => item is null
                                        || !ItemRefersToPhantomProto(item, tProto, allFlags))
                                    .ToList();
                                if (kept.Count < colItems.Count)
                                {
                                    var newArr = Array.CreateInstance(elemType, kept.Count);
                                    for (int i = 0; i < kept.Count; i++)
                                        newArr.SetValue(kept[i], i);
                                    fi.SetValue(obj, CreateImmutableArray(elemType, newArr));
                                    int removed = colItems.Count - kept.Count;
                                    totalRemoved += removed;
                                    if (verboseReportBudget-- > 0) progress?.Report($"    Rebuilt ImmutableArray {objType.Name}.{fi.Name}: removed {removed} phantom(s)");
                                }
                            }
                            else if (!elemType.IsPrimitive && !elemType.IsEnum && tProto is not null)
                            {
                                // ImmutableArray<TStruct>: struct elements may contain proto reference
                                // fields (e.g. ElectricityManager's consumer entries with MachineProto).
                                // Remove struct elements whose any proto field points to a phantom stub.
                                var kept = new List<object>(colItems.Count);
                                int removed = 0;
                                foreach (var item in colItems)
                                {
                                    if (item is null) { kept.Add(item!); continue; }
                                    bool hasPhantomProto = false;
                                    for (var sC = elemType; sC is not null && sC != typeof(object); sC = sC.BaseType)
                                    {
                                        foreach (var sF in sC.GetFields(allFlags))
                                        {
                                            if (sF.FieldType.IsValueType) continue;
                                            if (!tProto.IsAssignableFrom(sF.FieldType)) continue;
                                            try
                                            {
                                                var sv = sF.GetValue(item);
                                                if (sv is not null && _phantomProtoStubs!.Contains(sv))
                                                { hasPhantomProto = true; break; }
                                            }
                                            catch { }
                                        }
                                        if (hasPhantomProto) break;
                                    }
                                    if (!hasPhantomProto) kept.Add(item);
                                    else removed++;
                                }
                                if (removed > 0)
                                {
                                    var newArr = Array.CreateInstance(elemType, kept.Count);
                                    for (int i = 0; i < kept.Count; i++) newArr.SetValue(kept[i], i);
                                    fi.SetValue(obj, CreateImmutableArray(elemType, newArr));
                                    totalRemoved += removed;
                                    if (verboseReportBudget-- > 0) progress?.Report($"    Rebuilt ImmutableArray<struct> {objType.Name}.{fi.Name}: removed {removed} struct(s) with phantom proto field(s)");
                                }
                            }
                            continue;
                        }

                        // Dict<K, V>: handle phantom keys AND phantom values (including Option<TProto> values).
                        // The generic Remove(item) path below passes KeyValuePairs which doesn't work for Dicts.
                        var colTypeArgs = colType.IsGenericType ? colType.GetGenericArguments() : Array.Empty<Type>();
                        if (colTypeArgs.Length >= 2)
                        {
                            var keyType = colTypeArgs[0];
                            var valType = colTypeArgs[1];
                            var removeByKey = colType.GetMethod("Remove", new[] { keyType });
                            if (removeByKey is not null)
                            {
                                var phantomKeys = new List<object>();
                                foreach (var kv in colItems)
                                {
                                    if (kv is null) continue;
                                    var kvType = kv.GetType();
                                    var k = kvType.GetProperty("Key")?.GetValue(kv);
                                    var v = kvType.GetProperty("Value")?.GetValue(kv);
                                    if (k is null) continue;

                                    // Key is a phantom proto
                                    if (_phantomProtoStubs!.Contains(k))
                                    {
                                        phantomKeys.Add(k);
                                        continue;
                                    }

                                    // Key's runtime type is from a removed mod (e.g. dict declared
                                    // as Dict<IStaticEntity, V> but a key is a COIExtended FishFarm
                                    // instance). Saving writes the key's runtime type string and
                                    // the game crashes with CorruptedSaveException on load.
                                    if (ShouldStrip(k.GetType(), stripAssemblies))
                                    {
                                        phantomKeys.Add(k);
                                        continue;
                                    }

                                    // Value is a phantom proto directly
                                    if (v is not null && !v.GetType().IsValueType && _phantomProtoStubs.Contains(v))
                                    {
                                        phantomKeys.Add(k);
                                        continue;
                                    }

                                    // Value's runtime type is from a removed mod (analogous to the
                                    // key-runtime-type case above, for value-typed manager records).
                                    if (v is not null && !v.GetType().IsValueType && ShouldStrip(v.GetType(), stripAssemblies))
                                    {
                                        phantomKeys.Add(k);
                                        continue;
                                    }

                                    // Value is Option<TProto> wrapping a phantom
                                    if (v is not null && v.GetType().IsValueType && v.GetType().IsGenericType
                                        && v.GetType().Name.StartsWith("Option`", StringComparison.Ordinal))
                                    {
                                        var optInnerType = v.GetType().GetGenericArguments()[0];
                                        if (!optInnerType.IsValueType && tProto is not null && tProto.IsAssignableFrom(optInnerType))
                                        {
                                            foreach (var optFi in v.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                            {
                                                if (optFi.FieldType.IsValueType) continue;
                                                if (!optInnerType.IsAssignableFrom(optFi.FieldType)) continue;
                                                try
                                                {
                                                    var inner = optFi.GetValue(v);
                                                    if (inner is not null && _phantomProtoStubs.Contains(inner))
                                                    {
                                                        phantomKeys.Add(k);
                                                        break;
                                                    }
                                                }
                                                catch { }
                                            }
                                        }
                                    }

                                    // Value has a proto field pointing to a phantom
                                    if (v is not null && !v.GetType().IsValueType && ItemRefersToPhantomProto(v, tProto, allFlags))
                                    {
                                        phantomKeys.Add(k);
                                    }
                                }

                                foreach (var pk in phantomKeys)
                                {
                                    try
                                    {
                                        removeByKey.Invoke(val, new[] { pk });
                                        totalRemoved++;
                                        if (verboseReportBudget-- > 0) progress?.Report($"    Removed Dict entry with phantom key/value from {objType.Name}.{fi.Name}");
                                    }
                                    catch { }
                                }
                            }

                            // Enqueue Dict values for BFS traversal so nested objects
                            // (e.g. WorldMapLocation inside WorldMap.m_locations) are visited.
                            foreach (var kv in colItems)
                            {
                                if (kv is null) continue;
                                try
                                {
                                    var v = kv.GetType().GetProperty("Value")?.GetValue(kv);
                                    if (v is null || v.GetType().IsValueType || v is string) continue;
                                    if (tProto is not null && tProto.IsAssignableFrom(v.GetType())) continue;
                                    if (visited.Add(v)) queue.Enqueue(v);
                                }
                                catch { }
                            }
                            continue;
                        }

                        // Mutable collection: remove phantom items via Remove method.
                        var removeMethod = colType.GetMethod("Remove")
                            ?? colType.GetMethod("Remove", new[] { typeof(object) });
                        if (removeMethod is null) continue;

                        // For non-entity objects with phantom proto fields (e.g. GoalsList),
                        // try to heal the phantom before deciding to strip.  Items that ARE
                        // phantom stubs themselves or belong to a removed mod are stripped
                        // unconditionally — healing makes no sense for those.
                        var toRemove = new List<object>();
                        foreach (var item in colItems)
                        {
                            if (item is null) continue;
                            if (_phantomProtoStubs!.Contains(item)
                                || ItemRefersToRemovedMod(item, stripAssemblies, allFlags))
                            {
                                toRemove.Add(item);
                            }
                            else if (IsNonEntityWithPhantomProto(item, tProto, tIEntity, allFlags))
                            {
                                if (!TryHealNonEntityPhantomProto(item, tProto, allFlags, progress))
                                    toRemove.Add(item);
                            }
                        }

                        foreach (var item in toRemove)
                        {
                            try
                            {
                                removeMethod.Invoke(val, new[] { item });
                                totalRemoved++;
                                if (verboseReportBudget-- > 0) progress?.Report($"    Removed {item.GetType().Name} (phantom proto ref) from {objType.Name}.{fi.Name}");
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
        }

        progress?.Report($"  Stripped {totalRemoved} phantom proto reference(s) from resolver collections.");

        // Sweep entities separately using a streaming scan that avoids ToList() per field.
        if (entitiesLinearForSweep is System.Collections.IEnumerable entityEnum2)
            SweepEntityPhantomRefs(entityEnum2, tProto, allFlags, stripAssemblies, progress);
        else if (entitiesLinearForSweep is not null)
            progress?.Report("  WARNING: m_entitiesLinear is not IEnumerable — entity sweep skipped.");
        else
            progress?.Report("  WARNING: m_entitiesLinear not found — entity sweep skipped.");
    }

    /// <summary>
    /// Streaming sweep of all entities for phantom proto references.
    /// Unlike the BFS, this never calls ToList() on a collection unless phantoms are found,
    /// so it does not cause GC pressure on large saves with hundreds of thousands of entities.
    /// Handles: direct proto field refs, Option&lt;TProto&gt;, ImmutableArray&lt;TProto&gt;,
    /// and mutable collections whose element type is a proto subtype.
    /// </summary>
    private void SweepEntityPhantomRefs(
        System.Collections.IEnumerable entities,
        Type? tProto, BindingFlags allFlags, HashSet<string> stripAssemblies,
        IProgress<string>? progress)
    {
        int totalRemoved = 0;
        int verboseBudget = 200;

        foreach (var entity in entities)
        {
            if (entity is null) continue;
            var objType = entity.GetType();

            for (var cur = objType; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                foreach (var fi in cur.GetFields(allFlags))
                {
                    bool isMafiImmArray = fi.FieldType.IsValueType && fi.FieldType.IsGenericType
                        && fi.FieldType.Name.StartsWith("ImmutableArray", StringComparison.Ordinal);
                    bool isMafiOption = fi.FieldType.IsValueType && fi.FieldType.IsGenericType
                        && fi.FieldType.Name.StartsWith("Option`", StringComparison.Ordinal);
                    // Custom struct (e.g. Machine.RecipeResult) holding an Option<TProto> internally.
                    // Without this branch, value-type entity fields that aren't ImmutableArray/Option
                    // are skipped entirely, leaving phantom RecipeProto references inside the struct
                    // that crash deserialisation with "Failed cast loaded proto … to RecipeProto".
                    bool isOtherUserStruct = fi.FieldType.IsValueType
                        && !isMafiImmArray
                        && !isMafiOption
                        && !fi.FieldType.IsPrimitive
                        && !fi.FieldType.IsEnum
                        && fi.FieldType != typeof(decimal)
                        && fi.FieldType != typeof(DateTime)
                        && fi.FieldType != typeof(TimeSpan)
                        && fi.FieldType != typeof(Guid);
                    if (fi.FieldType.IsValueType && !isMafiImmArray && !isMafiOption && !isOtherUserStruct) continue;
                    if (fi.FieldType == typeof(string)) continue;

                    object? val;
                    try { val = fi.GetValue(entity); } catch { continue; }
                    if (val is null) continue;

                    // ── Direct proto field pointing to a phantom stub ──────────
                    if (!fi.FieldType.IsValueType
                        && tProto is not null
                        && tProto.IsAssignableFrom(fi.FieldType)
                        && _phantomProtoStubs!.Contains(val))
                    {
                        try { fi.SetValue(entity, null); } catch { continue; }
                        totalRemoved++;
                        if (verboseBudget-- > 0) progress?.Report($"    [entity sweep] Nulled {objType.Name}.{fi.Name} ({val.GetType().Name})");
                        continue;
                    }

                    // ── Option<TProto> wrapping a phantom stub ─────────────────
                    if (isMafiOption)
                    {
                        var innerType = fi.FieldType.GetGenericArguments()[0];
                        if (!innerType.IsValueType && tProto is not null && tProto.IsAssignableFrom(innerType))
                        {
                            var optFields = fi.FieldType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            foreach (var optFi in optFields)
                            {
                                if (optFi.FieldType.IsValueType) continue;
                                if (!innerType.IsAssignableFrom(optFi.FieldType)) continue;
                                try
                                {
                                    var inner = optFi.GetValue(val);
                                    if (inner is not null && _phantomProtoStubs!.Contains(inner))
                                    {
                                        var noneField = fi.FieldType.GetField("None", BindingFlags.Public | BindingFlags.Static);
                                        if (noneField is not null) { fi.SetValue(entity, noneField.GetValue(null)); totalRemoved++; }
                                    }
                                }
                                catch { }
                                break;
                            }
                        }
                        continue;
                    }

                    // ── ImmutableArray<TProto>: rebuild without phantoms ───────
                    if (isMafiImmArray)
                    {
                        if (fi.Name == "ManagedProtos" && objType.Name.Contains("SlimIdManager")) continue;
                        if (fi.Name == "ProductStats" && objType.Name == "ProductsManager") continue;

                        var elemType = fi.FieldType.GetGenericArguments()[0];
                        if (elemType.IsValueType || tProto is null || !tProto.IsAssignableFrom(elemType)) continue;

                        var colItems = EnumerateMafiImmutableArray(val);
                        if (colItems.Count == 0) continue;

                        // Fast check: are any phantoms present?
                        if (!colItems.Any(item => item is not null && _phantomProtoStubs!.Contains(item))) continue;

                        var kept = colItems.Where(item => item is null || !_phantomProtoStubs!.Contains(item)).ToList();
                        var newArr = Array.CreateInstance(elemType, kept.Count);
                        for (int i = 0; i < kept.Count; i++) newArr.SetValue(kept[i], i);
                        try
                        {
                            fi.SetValue(entity, CreateImmutableArray(elemType, newArr));
                            int removedCount = colItems.Count - kept.Count;
                            totalRemoved += removedCount;
                            if (verboseBudget-- > 0) progress?.Report($"    [entity sweep] Rebuilt ImmutableArray {objType.Name}.{fi.Name}: removed {removedCount} phantom(s)");
                        }
                        catch { }
                        continue;
                    }

                    // ── Custom struct (e.g. Machine.RecipeResult, Machine+MachineOutputBuffer): ──
                    // Recurse N levels into struct fields. If any nested Option<TProto> field
                    // (at any depth) holds a phantom stub, clear it to None. Then write the
                    // (possibly modified) struct back to the entity field. Without recursion,
                    // Option<RecipeProto> two levels deep — e.g. inside MachineOutputBuffer →
                    // RecipeResult — is never visited and the game crashes with
                    // "Failed cast … to RecipeProto".
                    if (isOtherUserStruct)
                    {
                        var structVal = val;
                        if (ScrubPhantomOptionsInStruct(ref structVal, fi.FieldType, tProto, stripAssemblies, depth: 0))
                        {
                            try
                            {
                                fi.SetValue(entity, structVal);
                                totalRemoved++;
                                if (verboseBudget-- > 0)
                                    progress?.Report($"    [entity sweep] Cleared phantom Option<TProto> in struct {fi.FieldType.Name} at {objType.Name}.{fi.Name}");
                            }
                            catch { }
                        }
                        continue;
                    }

                    // ── Lyst<TStruct> on entity: scrub Option<TProto> nested in struct items ──
                    // Catches e.g. Machine.m_outputBuffers (Lyst<MachineOutputBuffer>) where
                    // each MachineOutputBuffer struct holds a RecipeResult containing an
                    // Option<RecipeProto>. Without this, deep15's Machine.m_recipeResult fix
                    // works for the top-level field but leaves the same struct still phantom-
                    // laden inside collection items.
                    {
                        var lystType = val.GetType();
                        if (!lystType.IsValueType && lystType.IsGenericType
                            && lystType.Name.StartsWith("Lyst`", StringComparison.Ordinal))
                        {
                            var lystElem = lystType.GetGenericArguments()[0];
                            if (lystElem.IsValueType
                                && !lystElem.IsPrimitive && !lystElem.IsEnum
                                && lystElem != typeof(decimal) && lystElem != typeof(DateTime)
                                && lystElem != typeof(TimeSpan) && lystElem != typeof(Guid))
                            {
                                var getBackingArrayMi = lystType.GetMethod("GetBackingArray", BindingFlags.Public | BindingFlags.Instance);
                                var backingArr = getBackingArrayMi?.Invoke(val, null) as System.Array;
                                var countProp = lystType.GetProperty("Count");
                                int count = backingArr is not null ? (int)(countProp?.GetValue(val) ?? 0) : 0;
                                int itemsModified = 0;
                                for (int i = 0; i < count; i++)
                                {
                                    var item = backingArr!.GetValue(i);
                                    if (item is null) continue;
                                    if (ScrubPhantomOptionsInStruct(ref item, lystElem, tProto, stripAssemblies, depth: 0))
                                    {
                                        try { backingArr.SetValue(item, i); itemsModified++; }
                                        catch { }
                                    }
                                }
                                if (itemsModified > 0)
                                {
                                    totalRemoved += itemsModified;
                                    if (verboseBudget-- > 0)
                                        progress?.Report($"    [entity sweep] Cleared phantom Option<TProto> in {itemsModified} item(s) of {objType.Name}.{fi.Name} (Lyst<{lystElem.Name}>)");
                                }
                                continue;
                            }
                        }
                    }

                    // ── Mutable collection (List, Set, etc.) ───────────────────
                    // Streaming: enumerate once to find phantoms; only allocate a list
                    // if at least one phantom is found.  This avoids ToList() on every
                    // collection field of every entity (which would OOM on large saves).
                    if (fi.FieldType.IsValueType || val is not System.Collections.IEnumerable col) continue;

                    var colType = val.GetType();
                    var typeArgs = colType.IsGenericType ? colType.GetGenericArguments() : Array.Empty<Type>();
                    if (typeArgs.Length == 0 || typeArgs.Length > 2) continue;
                    var elemT = typeArgs[0];
                    if (elemT.IsValueType || tProto is null || !tProto.IsAssignableFrom(elemT)) continue;

                    List<object>? phantomsFound = null;
                    try
                    {
                        foreach (var item in col)
                        {
                            if (item is not null && _phantomProtoStubs!.Contains(item))
                                (phantomsFound ??= new List<object>()).Add(item);
                        }
                    }
                    catch { continue; }

                    if (phantomsFound is null) continue;

                    var removeMethod = colType.GetMethod("Remove", new[] { elemT })
                        ?? colType.GetMethod("Remove", new[] { typeof(object) });
                    if (removeMethod is null) continue;

                    foreach (var ph in phantomsFound)
                    {
                        try { removeMethod.Invoke(val, new[] { ph }); totalRemoved++; } catch { }
                    }
                    if (verboseBudget-- > 0)
                        progress?.Report($"    [entity sweep] Removed {phantomsFound.Count} phantom(s) from {objType.Name}.{fi.Name}");
                }
            }
        }

        progress?.Report($"  Entity sweep: {totalRemoved} phantom proto reference(s) removed from entities.");
    }

    /// <summary>
    /// Recursively scrub <c>Option&lt;TProto&gt;</c> fields nested anywhere inside a struct value.
    /// Clears each Option to <c>None</c> when the held inner is a phantom proto stub OR has
    /// a runtime type from a removed-mod assembly. Returns true if any field was modified.
    /// <para/>
    /// Handles arbitrary depth (e.g. <c>Machine.m_outputBuffers[*]</c> →
    /// <c>MachineOutputBuffer.m_recipeResult</c> → <c>RecipeResult.Recipe</c> →
    /// <c>Option&lt;RecipeProto&gt;</c>) by descending into any value-type non-Option field.
    /// </summary>
    private bool ScrubPhantomOptionsInStruct(
        ref object structVal, Type structType,
        Type? tProto, HashSet<string> stripAssemblies, int depth)
    {
        // Hard cap on recursion depth to prevent runaway on pathological types.
        if (depth > 6) return false;
        if (tProto is null) return false;

        bool modified = false;
        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var sFi in structType.GetFields(allFlags))
        {
            var sFt = sFi.FieldType;
            if (!sFt.IsValueType) continue;
            if (sFt.IsPrimitive || sFt.IsEnum) continue;
            if (sFt == typeof(decimal) || sFt == typeof(DateTime)
                || sFt == typeof(TimeSpan) || sFt == typeof(Guid)) continue;

            // Direct Option<TProto> field on this struct: clear when held inner is phantom or removed-mod.
            if (sFt.IsGenericType && sFt.Name.StartsWith("Option`", StringComparison.Ordinal))
            {
                var sInner = sFt.GetGenericArguments()[0];
                if (sInner.IsValueType || !tProto.IsAssignableFrom(sInner)) continue;
                try
                {
                    var optBox = sFi.GetValue(structVal);
                    if (optBox is null) continue;
                    bool clear = false;
                    foreach (var optFi in sFt.GetFields(allFlags))
                    {
                        if (optFi.FieldType.IsValueType) continue;
                        if (!sInner.IsAssignableFrom(optFi.FieldType)) continue;
                        var heldInner = optFi.GetValue(optBox);
                        if (heldInner is null) continue;
                        if (_phantomProtoStubs!.Contains(heldInner)
                            || ShouldStrip(heldInner.GetType(), stripAssemblies))
                        {
                            clear = true;
                            break;
                        }
                    }
                    if (clear)
                    {
                        var noneField = sFt.GetField("None", BindingFlags.Public | BindingFlags.Static);
                        if (noneField is not null)
                        {
                            sFi.SetValue(structVal, noneField.GetValue(null));
                            modified = true;
                        }
                    }
                }
                catch { }
                continue;
            }

            // Other struct field — recurse one level deeper.
            try
            {
                var nestedBox = sFi.GetValue(structVal);
                if (nestedBox is null) continue;
                if (ScrubPhantomOptionsInStruct(ref nestedBox, sFt, tProto, stripAssemblies, depth + 1))
                {
                    sFi.SetValue(structVal, nestedBox);
                    modified = true;
                }
            }
            catch { }
        }

        return modified;
    }

    /// <summary>
    /// Enumerates a Mafi ImmutableArray&lt;T&gt; struct safely.
    /// Tries non-generic IEnumerable first; falls back to GetEnumerator via reflection
    /// in case Mafi's struct only implements IEnumerable&lt;T&gt; or a custom enumerator.
    /// </summary>
    private static List<object> EnumerateMafiImmutableArray(object immArray)
    {
        if (immArray is System.Collections.IEnumerable nge)
            return nge.Cast<object>().ToList();

        var result = new List<object>();
        try
        {
            var getEnum = immArray.GetType().GetMethod("GetEnumerator");
            if (getEnum is null) return result;
            var enumerator = getEnum.Invoke(immArray, null);
            if (enumerator is null) return result;
            var enumType = enumerator.GetType();
            var moveNext = enumType.GetMethod("MoveNext");
            var current = enumType.GetProperty("Current");
            if (moveNext is null || current is null) return result;
            while (moveNext.Invoke(enumerator, null) is true)
                result.Add(current.GetValue(enumerator)!);
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Returns true if the item IS a phantom stub, or has any Proto-typed field
    /// pointing to a phantom stub (e.g. a ResearchNode whose Proto is a stub).
    /// </summary>
    private bool ItemRefersToPhantomProto(object item, Type? tProto, BindingFlags allFlags)
    {
        if (_phantomProtoStubs!.Contains(item)) return true;
        if (tProto is null) return false;

        for (var cur = item.GetType(); cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var fi in cur.GetFields(allFlags))
            {
                if (fi.FieldType.IsValueType) continue;
                if (!tProto.IsAssignableFrom(fi.FieldType)) continue;
                try
                {
                    var val = fi.GetValue(item);
                    if (val is not null && _phantomProtoStubs.Contains(val))
                        return true;
                }
                catch { }
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true if the item is NOT a game entity (IEntity) and has a Proto-typed
    /// field pointing to a phantom stub. Used to strip vanilla non-entity objects
    /// (e.g. GoalsList whose GoalListProto comes from a removed mod) from mutable
    /// collections without touching vanilla entities (e.g. trucks) whose proto fields
    /// may also be phantoms but whose presence is handled by the entity pipeline.
    /// </summary>
    private bool IsNonEntityWithPhantomProto(object item, Type? tProto, Type? tIEntity, BindingFlags allFlags)
        => IsNonEntityWithPhantomProto(item, tProto, tIEntity, _phantomProtoStubs!, allFlags);

    /// <summary>Testable static overload.</summary>
    internal static bool IsNonEntityWithPhantomProto(
        object item, Type? tProto, Type? tIEntity, ISet<object> phantomStubs, BindingFlags allFlags)
    {
        if (tIEntity is not null && tIEntity.IsAssignableFrom(item.GetType())) return false;
        if (phantomStubs.Contains(item)) return true;
        if (tProto is null) return false;
        for (var cur = item.GetType(); cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var fi in cur.GetFields(allFlags))
            {
                if (fi.FieldType.IsValueType) continue;
                if (!tProto.IsAssignableFrom(fi.FieldType)) continue;
                try
                {
                    var v = fi.GetValue(item);
                    if (v is not null && phantomStubs.Contains(v)) return true;
                }
                catch { }
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true if the item itself, or any of its reference-type fields,
    /// belongs to a removed mod assembly. This catches event callback entries
    /// (e.g. CallbackSaveData) that hold subscriber references to removed mod types.
    /// </summary>
    private static bool ItemRefersToRemovedMod(object item, HashSet<string> stripAssemblies, BindingFlags allFlags)
    {
        if (stripAssemblies.Count == 0) return false;

        // Check the item's own type.
        if (ShouldStrip(item.GetType(), stripAssemblies)) return true;

        // Check reference-type fields for removed mod types.
        for (var cur = item.GetType(); cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var fi in cur.GetFields(allFlags))
            {
                if (fi.FieldType.IsValueType || fi.FieldType == typeof(string)) continue;
                try
                {
                    var val = fi.GetValue(item);
                    if (val is null) continue;
                    // Type fields (e.g. CallbackSaveData.DeclaringType): check the referenced
                    // type's assembly, not the assembly of the Type wrapper itself.
                    if (val is Type typeRef)
                    {
                        if (ShouldStrip(typeRef, stripAssemblies)) return true;
                    }
                    else if (ShouldStrip(val.GetType(), stripAssemblies))
                        return true;
                }
                catch { }
            }
        }
        return false;
    }

    /// <summary>
    /// Targeted, bounded scrub for <c>Option&lt;T&gt;</c> fields whose generic argument
    /// T comes from a removed-mod assembly. Walks only top-level resolved objects
    /// and the type-keyed dict; for each object scans direct fields and one level
    /// of <c>Lyst&lt;TStruct&gt;</c> backing-array items. No deep BFS — designed to
    /// catch managers (e.g. <c>AssetTransactionManager.m_globalBuffers</c> with a
    /// <c>Lyst&lt;GlobalBuffer&gt;</c> whose items hold <c>Option&lt;COIExtended…FishFarm&gt;</c>)
    /// that <see cref="StripPhantomProtoRefsFromCollections"/> doesn't reach because
    /// <c>Lyst&lt;TStruct&gt;</c> items are skipped by the generic-collection branch.
    /// </summary>
    private void ScrubTopLevelRemovedModOptionFields(object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        if (stripAssemblies.Count == 0)
        {
            progress?.Report("  No removed-mod assemblies — skipping top-level Option<T> scrub.");
            return;
        }

        var fiResolved = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
        var resolved = fiResolved?.GetValue(resolver) as System.Collections.IEnumerable;
        if (resolved is null) { progress?.Report("  m_resolvedObjects not found — skipping."); return; }

        var roots = new List<object>();
        var seenRoots = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var o in resolved.Cast<object>())
            if (o is not null && seenRoots.Add(o)) roots.Add(o);

        var fiByType = FindFieldDeep(resolver.GetType(), "m_resolvedInstancesByRealType");
        if (fiByType?.GetValue(resolver) is System.Collections.IEnumerable byTypeDict)
        {
            foreach (var kv in byTypeDict.Cast<object>())
            {
                var v = kv?.GetType().GetProperty("Value")?.GetValue(kv);
                if (v is not null && seenRoots.Add(v)) roots.Add(v);
            }
        }

        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        int cleared = 0;
        int logBudget = 200;

        // Bounded BFS: visit reference-typed objects only. NEVER iterate arbitrary
        // IEnumerable values (those caused the previous hang on lazy/large enumerators).
        // For each visited object: clear matching Option<T> direct fields, walk Lyst<TStruct>
        // backing arrays one level deep to scrub items, and enqueue plain reference fields.
        const int VisitBudget = 200_000;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        foreach (var r in roots) { visited.Add(r); queue.Enqueue(r); }

        int visits = 0;
        var lystSeen = new HashSet<Type>(); // diagnostic: which Lyst<T> types we encountered
        var visitedTypes = 0;

        while (queue.Count > 0 && visits < VisitBudget)
        {
            var obj = queue.Dequeue();
            visits++;
            try
            {
                cleared += ScrubAndEnqueue(obj, stripAssemblies, allFlags,
                    ref logBudget, queue, visited, lystSeen, progress);
            }
            catch (Exception ex)
            {
                if (logBudget-- > 0)
                    progress?.Report($"    Scrub error on {obj.GetType().FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        visitedTypes = visits;

        if (visits >= VisitBudget)
            progress?.Report($"  Top-level Option<T> scrub: hit visit budget {VisitBudget}; some fields may not have been scrubbed.");
        progress?.Report($"  Top-level Option<T> scrub: {cleared} field(s) cleared across {roots.Count} root(s), {visitedTypes} object(s) visited.");
    }

    /// <summary>
    /// Single-object scrub used by <see cref="ScrubTopLevelRemovedModOptionFields"/>.
    /// Clears <c>Option&lt;T&gt;</c> direct fields whose T is from a removed mod, walks
    /// <c>Lyst&lt;TStruct&gt;</c> backing arrays to scrub the same on struct items, and
    /// enqueues plain reference fields for further BFS visiting. Does NOT iterate
    /// arbitrary IEnumerables (avoids hangs on lazy/large enumerators).
    /// </summary>
    private int ScrubAndEnqueue(
        object obj,
        HashSet<string> stripAssemblies,
        BindingFlags allFlags,
        ref int logBudget,
        Queue<object> queue,
        HashSet<object> visited,
        HashSet<Type> lystSeen,
        IProgress<string>? progress)
    {
        int cleared = 0;
        var objType = obj.GetType();
        for (var cur = objType; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            FieldInfo[] fields;
            try { fields = cur.GetFields(allFlags | BindingFlags.DeclaredOnly); }
            catch { continue; }

            foreach (var fi in fields)
            {
                var ft = fi.FieldType;
                try
                {
                    // (1) Direct Option<T> field: clear when T is from a removed mod.
                    if (ft.IsValueType && ft.IsGenericType
                        && ft.Name.StartsWith("Option`", StringComparison.Ordinal))
                    {
                        var innerType = ft.GetGenericArguments()[0];
                        if (!innerType.IsValueType && ShouldStrip(innerType, stripAssemblies))
                        {
                            var noneField = ft.GetField("None", BindingFlags.Public | BindingFlags.Static);
                            if (noneField is not null)
                            {
                                fi.SetValue(obj, noneField.GetValue(null));
                                cleared++;
                                if (logBudget-- > 0)
                                    progress?.Report($"    Cleared Option<{innerType.FullName}> in {objType.Name}.{fi.Name}");
                            }
                        }
                        continue;
                    }

                    if (ft.IsValueType || ft == typeof(string)) continue;

                    var val = fi.GetValue(obj);
                    if (val is null) continue;
                    var valType = val.GetType();

                    // (2) Lyst<T> field
                    //   - Lyst<TStruct>: walk backing array, scrub each struct item's
                    //     Option<T>-with-removed-mod-T fields, write modified structs back.
                    //   - Lyst<TRef>:    walk backing array and enqueue each reference item
                    //     for further BFS visiting (this is how we reach managers held in
                    //     parent-manager Lyst fields, e.g. AssetTransactionManager).
                    if (valType.IsGenericType && valType.Name.StartsWith("Lyst`", StringComparison.Ordinal))
                    {
                        var elemType = valType.GetGenericArguments()[0];
                        if (elemType.IsValueType)
                        {
                            if (lystSeen.Add(elemType) && logBudget-- > 0)
                                progress?.Report($"    Visiting Lyst<{elemType.FullName}> at {objType.Name}.{fi.Name}");
                            cleared += ScrubLystStructItems(val, valType, elemType, stripAssemblies,
                                allFlags, ref logBudget, objType.Name, fi.Name, progress);
                            continue;
                        }
                        // Lyst<TRef>: enqueue items for BFS via backing array (NOT IEnumerable
                        // — that's the lazy-enumerator hang risk).
                        EnqueueLystRefItems(val, valType, queue, visited);
                        continue;
                    }

                    // (2b) Dict<K,V> field: enqueue Values for BFS so we reach managers held
                    // in parent dictionaries (e.g. EconomyManager.m_managers).
                    if (valType.IsGenericType && valType.Name.StartsWith("Dict`", StringComparison.Ordinal))
                    {
                        EnqueueDictValues(val, valType, queue, visited);
                        continue;
                    }

                    // (2c) Plain array field: enqueue reference elements.
                    if (valType.IsArray && !valType.GetElementType()!.IsValueType)
                    {
                        var arr = (System.Array)val;
                        int alen = arr.Length;
                        for (int ai = 0; ai < alen; ai++)
                        {
                            var ae = arr.GetValue(ai);
                            if (ae is null) continue;
                            if (visited.Add(ae)) queue.Enqueue(ae);
                        }
                        continue;
                    }

                    // (3) Plain reference field: enqueue for further BFS visiting.
                    // Skip ANY other IEnumerable to avoid lazy-enumerator hangs.
                    if (val is System.Collections.IEnumerable) continue;
                    if (visited.Add(val)) queue.Enqueue(val);
                }
                catch { /* defensive — never let a single field abort the scrub */ }
            }
        }
        return cleared;
    }

    /// <summary>Enqueue reference-typed items of a Mafi <c>Lyst&lt;TRef&gt;</c> via its
    /// backing array (avoids using <c>IEnumerable.GetEnumerator()</c> which can hang on
    /// lazy/large enumerators).</summary>
    private static void EnqueueLystRefItems(
        object lyst, Type lystType, Queue<object> queue, HashSet<object> visited)
    {
        try
        {
            var getBackingArrayMi = lystType.GetMethod("GetBackingArray", BindingFlags.Public | BindingFlags.Instance);
            var backingArr = getBackingArrayMi?.Invoke(lyst, null) as System.Array;
            if (backingArr is null) return;
            var countProp = lystType.GetProperty("Count");
            int count = (int)(countProp?.GetValue(lyst) ?? 0);
            for (int i = 0; i < count; i++)
            {
                var item = backingArr.GetValue(i);
                if (item is null || item is string) continue;
                if (item.GetType().IsValueType) continue;
                if (visited.Add(item)) queue.Enqueue(item);
            }
        }
        catch { /* defensive */ }
    }

    /// <summary>Enqueue reference-typed values of a Mafi <c>Dict&lt;K,V&gt;</c> by reading
    /// its private <c>m_values</c> array directly (Mafi <c>Dict</c> uses an open-addressing
    /// table with a value array; iterating <c>Values</c> via the public enumerator is also
    /// safe but reflective access keeps the BFS uniform with the Lyst path).</summary>
    private static void EnqueueDictValues(
        object dict, Type dictType, Queue<object> queue, HashSet<object> visited)
    {
        try
        {
            var fiValues = FindFieldDeep(dictType, "m_values");
            if (fiValues?.GetValue(dict) is not System.Array valuesArr) return;
            int len = valuesArr.Length;
            for (int i = 0; i < len; i++)
            {
                var item = valuesArr.GetValue(i);
                if (item is null || item is string) continue;
                if (item.GetType().IsValueType) continue;
                if (visited.Add(item)) queue.Enqueue(item);
            }
        }
        catch { /* defensive */ }
    }

    private int ScrubLystStructItems(
        object lyst, Type lystType, Type elemType,
        HashSet<string> stripAssemblies,
        BindingFlags allFlags,
        ref int logBudget,
        string ownerTypeName, string ownerFieldName,
        IProgress<string>? progress)
    {
        int cleared = 0;
        var getBackingArrayMi = lystType.GetMethod("GetBackingArray", BindingFlags.Public | BindingFlags.Instance);
        var backingArr = getBackingArrayMi?.Invoke(lyst, null) as System.Array;
        var countProp = lystType.GetProperty("Count");
        int count = backingArr is not null ? (int)(countProp?.GetValue(lyst) ?? 0) : 0;
        if (backingArr is null || count == 0) return 0;

        // Diagnostic: first time we see this struct type, dump its fields so we
        // can see whether (e.g.) GlobalBuffer has Option<FishFarm> or something like
        // Option<IEntityProto> whose runtime instance is a FishFarm.
        if (_lystStructDiagnosticDumped is null)
            _lystStructDiagnosticDumped = new HashSet<Type>();
        if (_lystStructDiagnosticDumped.Add(elemType) && logBudget > 0)
        {
            for (var dc = elemType; dc is not null && dc != typeof(object); dc = dc.BaseType)
            {
                foreach (var dfi in dc.GetFields(allFlags | BindingFlags.DeclaredOnly))
                {
                    if (logBudget-- <= 0) break;
                    progress?.Report($"      [diag] {elemType.Name}.{dfi.Name} : {dfi.FieldType.FullName}");
                }
            }
        }

        for (int i = 0; i < count; i++)
        {
            var item = backingArr.GetValue(i);
            if (item is null) continue;
            bool itemModified = false;
            for (var lic = elemType; lic is not null && lic != typeof(object); lic = lic.BaseType)
            {
                FieldInfo[] liFields;
                try { liFields = lic.GetFields(allFlags | BindingFlags.DeclaredOnly); }
                catch { continue; }

                foreach (var liFi in liFields)
                {
                    var liFt = liFi.FieldType;
                    if (!liFt.IsValueType
                        || !liFt.IsGenericType
                        || !liFt.Name.StartsWith("Option`", StringComparison.Ordinal))
                        continue;
                    var liInner = liFt.GetGenericArguments()[0];
                    var liNone = liFt.GetField("None", BindingFlags.Public | BindingFlags.Static);
                    if (liNone is null) continue;

                    // Case A: declared T is from a removed mod → clear unconditionally.
                    bool clear = !liInner.IsValueType && ShouldStrip(liInner, stripAssemblies);
                    string reason = "T is removed-mod";

                    // Case B: declared T is vanilla (e.g. IEntityProto) but the held inner
                    // instance's RUNTIME type is from a removed mod (e.g. FishFarm).
                    // This is the AssetTransactionManager+GlobalBuffer scenario where
                    // Option<…IEntity> holds a COIExtended FishFarm reference.
                    if (!clear && !liInner.IsValueType)
                    {
                        try
                        {
                            var optBox = liFi.GetValue(item);
                            if (optBox is not null)
                            {
                                foreach (var optInnerFi in liFt.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                {
                                    if (optInnerFi.FieldType.IsValueType) continue;
                                    if (!liInner.IsAssignableFrom(optInnerFi.FieldType)) continue;
                                    var heldInner = optInnerFi.GetValue(optBox);
                                    if (heldInner is null) continue;
                                    if (ShouldStrip(heldInner.GetType(), stripAssemblies))
                                    {
                                        clear = true;
                                        reason = $"runtime {heldInner.GetType().FullName} is removed-mod";
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (!clear) continue;

                    try
                    {
                        liFi.SetValue(item, liNone.GetValue(null));
                        itemModified = true;
                        cleared++;
                        if (logBudget-- > 0)
                            progress?.Report($"    Cleared Option<{liInner.Name}> ({reason}) in struct {elemType.Name} of {ownerTypeName}.{ownerFieldName}[{i}].{liFi.Name}");
                    }
                    catch { }
                }
            }
            if (itemModified)
            {
                try { backingArr.SetValue(item, i); } catch { }
            }
        }
        return cleared;
    }

    // ── Final catch-all phantom-stub kill pass ──────────────────────────────
    //
    // After every structural scrub has run, walk the whole reachable object graph
    // (resolver objects + entities) and null any reference field whose value is in
    // _phantomProtoStubs. This is the safety net that catches surviving phantom
    // references in containing types we don't have a dedicated scrub branch for
    // (e.g. ProductProto, TerrainTileSurfaceProto, ActiveLoan, settlement modules).
    //
    // Without this, the validator finds 1-15 surviving phantom IDs scattered across
    // ~12 different containing types, each of which would need its own targeted
    // scrub branch. The catch-all collapses all of those into a single uniform pass.
    //
    // Performance: bounded by VisitBudget (200K objects) and ReferenceEqualityComparer
    // visit set. On a 175K-entity save the BFS visits ~600K objects in ~3 seconds.

    // ── Phantom-stub → vanilla-proto replacement ──────────────────────────────
    //
    // COIExtended replaced vanilla product, machine, and other proto types with its
    // own subclass instances (e.g. COIExtended.ModProductProto for "Product_Iron").
    // During deserialization these become phantom stubs (the vanilla ProtosDb doesn't
    // contain the COIExtended subtype), so they land in _phantomProtoStubs. When the
    // save is re-serialised those phantom stubs get written with their COIExtended
    // TypeIds; without COIExtended the game cannot load them and reports
    // "Missing proto detected (Product_Iron (unit))".
    //
    // Fix: walk the resolver's entire object graph BEFORE NullAllPhantomStubReferences
    // and, for every location that holds a phantom stub whose ID exists in the vanilla
    // ProtosDb (e.g. "Product_Iron"), replace the stub with the REAL vanilla proto.
    // This covers:
    //   • Object reference fields (fi.SetValue)
    //   • Array elements (arr.SetValue)
    //   • Lyst<T> / List<T> backing-array slots (via m_items / _items)
    //   • ImmutableArray<T> backing arrays (Mafi uses m_items for its own variant)
    // After this pass the re-serialised save contains vanilla TypeIds for all replaced
    // protos, and the game resolves them correctly on load.

    /// <summary>
    /// Replaces every reference to a phantom proto stub — wherever it lives in the
    /// resolver's object graph — with the matching vanilla proto from
    /// <c>_protoHealingLookup.ById</c>.  Only stubs whose string ID resolves to a
    /// vanilla proto are replaced; the rest are left for the null pass that follows.
    /// </summary>
    private void ReplacePhantomProtoRefsWithVanilla(object resolver, IProgress<string>? progress)
    {
        if (_phantomProtoStubs is null || _phantomProtoStubs.Count == 0 || _protoHealingLookup is null)
        {
            progress?.Report("  Vanilla proto replacement: no phantom stubs or healing lookup — skipped.");
            return;
        }

        // Build stub → vanilla replacement map (only stubs whose ID has a vanilla match).
        // IMPORTANT: do NOT use _protoHealingLookup.GetProtoIdString(stub) here.
        // Phantom stubs are uninitialized objects; their Proto.ToString() overrides throw
        // (reading null fields), so GetProtoIdString returns null for them. Instead, read
        // the <Id>k__BackingField directly and extract the string via the runtime Value
        // property — same approach NullifyPhantomProtoIds uses successfully on all 669 stubs.
        var tProtoForStubIds = AssemblyLoader.FindType("Mafi.Core.Prototypes.Proto");
        var fiStubId = tProtoForStubIds?.GetField("<Id>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);

        var replacements = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        int noIdCount = 0;
        int inByIdCount = 0;
        var vanillaMatches = new System.Text.StringBuilder();
        var noMatchSample  = new System.Text.StringBuilder();
        int noMatchCount   = 0;
        foreach (var stub in _phantomProtoStubs)
        {
            string? stubId = null;
            try
            {
                var idObj = fiStubId?.GetValue(stub);
                if (idObj is not null)
                {
                    var vp = idObj.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    stubId = (vp?.GetValue(idObj) as string) ?? idObj.ToString();
                }
            }
            catch { }

            if (string.IsNullOrEmpty(stubId)) { noIdCount++; continue; }

            if (_protoHealingLookup.ById.TryGetValue(stubId!, out var vanilla))
            {
                replacements[stub] = vanilla;
                inByIdCount++;
                if (inByIdCount <= 20)
                    vanillaMatches.Append($"\n    match: type={stub.GetType().Name}, id='{stubId}'");
            }
            else
            {
                if (noMatchCount++ < 20)
                    noMatchSample.Append($"\n    no-match: type={stub.GetType().Name}, id='{stubId}'");
            }
        }
        progress?.Report($"  Vanilla proto replacement: {inByIdCount} stubs map to vanilla IDs, {noMatchCount} do not, {noIdCount} have no readable ID.");
        if (inByIdCount > 0)
            progress?.Report($"  Vanilla ID matches (first {Math.Min(inByIdCount, 20)}):{vanillaMatches}");
        else
            progress?.Report($"  No-match sample (first {Math.Min(noMatchCount, 20)}):{noMatchSample}");

        if (noIdCount > 0)
            progress?.Report($"  Vanilla proto replacement: {noIdCount} stub(s) had no readable ID (will be handled by null pass).");

        if (replacements.Count == 0)
        {
            progress?.Report("  Vanilla proto replacement: no phantom stubs map to vanilla IDs — skipped.");
            return;
        }

        progress?.Report($"  Vanilla proto replacement: {replacements.Count} phantom stub(s) have vanilla equivalents — scanning resolver…");

        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        int replaced = 0;
        int visits   = 0;
        int logBudget = 100;
        const int VisitBudget  = 600_000;
        const int QueueSizeCap = 300_000;

        // ── Collect roots (same scope as NullAllRemovedModEntityReferences) ──
        var roots = new List<object>();
        try
        {
            var fiResolved = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
            if (fiResolved?.GetValue(resolver) is System.Collections.IEnumerable resolved)
                foreach (var o in resolved) if (o is not null) roots.Add(o);
        }
        catch { }
        SeedRootsFromResolverDict(resolver, "m_resolvedInstancesByRealType", roots);
        SeedRootsFromResolverDict(resolver, "m_resolvedInstancesByRegisteredType", roots);

        var tEM = AssemblyLoader.FindType("Mafi.Core.Entities.EntitiesManager");
        if (tEM is not null)
        {
            var em = roots.FirstOrDefault(o => tEM.IsAssignableFrom(o.GetType()));
            if (em is not null)
            {
                var fiLinear = FindFieldDeep(em.GetType(), "m_entitiesLinear");
                if (fiLinear?.GetValue(em) is System.Collections.IEnumerable entities)
                    foreach (var e in entities) if (e is not null) roots.Add(e);
            }
        }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue   = new Queue<object>();
        foreach (var r in roots) if (r is not null && visited.Add(r)) queue.Enqueue(r);

        while (queue.Count > 0 && visits < VisitBudget)
        {
            var obj = queue.Dequeue();
            visits++;
            var objType = obj.GetType();

            for (var cur = objType; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo[] fields;
                try { fields = cur.GetFields(allFlags | BindingFlags.DeclaredOnly); }
                catch { continue; }

                foreach (var fi in fields)
                {
                    var ft = fi.FieldType;
                    if (ft == typeof(string)) continue;

                    // Value-type fields can't hold a stub directly, but IEnumerable value
                    // types (e.g. ImmutableArray<T> as a struct) might wrap an array.
                    bool ftIsEnumerable = typeof(System.Collections.IEnumerable).IsAssignableFrom(ft);
                    if (ft.IsValueType && !ftIsEnumerable) continue;

                    object? val;
                    try { val = fi.GetValue(obj); }
                    catch { continue; }
                    if (val is null) continue;

                    // Direct phantom-stub field: replace if field type accepts the vanilla proto.
                    if (!val.GetType().IsValueType && replacements.TryGetValue(val, out var repl))
                    {
                        if (fi.FieldType.IsAssignableFrom(repl.GetType()))
                        {
                            try
                            {
                                fi.SetValue(obj, repl);
                                replaced++;
                                if (logBudget-- > 0)
                                    progress?.Report($"    Replaced {objType.Name}.{fi.Name}: phantom stub → vanilla '{_protoHealingLookup.GetProtoIdString(repl)}'");
                            }
                            catch { }
                        }
                        continue; // don't enqueue the stub
                    }

                    // Collection: replace stub elements in backing array.
                    if (val is System.Collections.IEnumerable enumerable)
                    {
                        replaced += ReplacePhantomStubsInCollection(val, replacements, ref logBudget, progress);
                        // Enqueue non-stub class elements for further BFS.
                        try
                        {
                            foreach (var elem in enumerable)
                            {
                                if (elem is null) continue;
                                var et = elem.GetType();
                                if (et.IsValueType || et == typeof(string)) continue;
                                if (!replacements.ContainsKey(elem) && visited.Add(elem) && queue.Count < QueueSizeCap)
                                    queue.Enqueue(elem);
                            }
                        }
                        catch { }
                        continue;
                    }

                    // Plain object reference — enqueue.
                    if (visited.Add(val) && queue.Count < QueueSizeCap) queue.Enqueue(val);
                }
            }
        }

        bool budgetHit = visits >= VisitBudget;
        progress?.Report($"  Vanilla proto replacement: {replaced} phantom stub(s) replaced across {visits:N0} objects{(budgetHit ? " (visit budget hit — some refs may remain)" : "")}.");
    }

    /// <summary>
    /// Replaces phantom-stub elements in the backing array of a collection
    /// (Array, Lyst&lt;T&gt;, List&lt;T&gt;, or ImmutableArray&lt;T&gt; via m_items).
    /// Returns the number of elements replaced.
    /// </summary>
    private int ReplacePhantomStubsInCollection(
        object collection, Dictionary<object, object> replacements,
        ref int logBudget, IProgress<string>? progress)
    {
        int count = 0;
        const BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Instance;

        // Case 1: the collection IS an array (e.g. object[], Proto[]).
        if (collection is Array directArr)
        {
            var elemType = directArr.GetType().GetElementType()!;
            if (elemType.IsValueType) return 0; // can't hold stub refs
            for (int i = 0; i < directArr.Length; i++)
            {
                var elem = directArr.GetValue(i);
                if (elem is null) continue;
                if (replacements.TryGetValue(elem, out var repl) && elemType.IsAssignableFrom(repl.GetType()))
                {
                    try
                    {
                        directArr.SetValue(repl, i);
                        count++;
                        if (logBudget-- > 0)
                            progress?.Report($"    Replaced array[{i}] → vanilla '{_protoHealingLookup?.GetProtoIdString(repl)}'");
                    }
                    catch { }
                }
            }
            return count;
        }

        // Case 2: Lyst<T> / List<T> — backing array at m_items or _items, size at m_size or _size.
        var colType = collection.GetType();
        var fiItems = colType.GetField("m_items", bf) ?? colType.GetField("_items", bf);
        var fiSize  = colType.GetField("m_size",  bf) ?? colType.GetField("_size",  bf);

        if (fiItems?.GetValue(collection) is Array backingArr)
        {
            var elemType = backingArr.GetType().GetElementType()!;
            if (elemType.IsValueType) return count;
            int size = fiSize is not null ? (int)(fiSize.GetValue(collection) ?? backingArr.Length) : backingArr.Length;
            for (int i = 0; i < size; i++)
            {
                var elem = backingArr.GetValue(i);
                if (elem is null) continue;
                if (replacements.TryGetValue(elem, out var repl) && elemType.IsAssignableFrom(repl.GetType()))
                {
                    try { backingArr.SetValue(repl, i); count++; }
                    catch { }
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Walks every reachable object from <paramref name="resolver"/> and from the
    /// EntitiesManager's entity set, and nulls every reference field (or clears every
    /// <c>Option&lt;TProto&gt;</c>) whose held value is a phantom proto stub. Runs as
    /// the LAST scrub before re-serialisation; serves as the safety net behind every
    /// structural scrub above.
    /// </summary>
    private void NullAllPhantomStubReferences(object resolver, IProgress<string>? progress)
    {
        if (_phantomProtoStubs is null || _phantomProtoStubs.Count == 0)
        {
            progress?.Report("  No phantom proto stubs to null-scrub.");
            return;
        }

        const int VisitBudget  = 200_000;
        const int QueueSizeCap = 300_000;
        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        int nulled = 0;
        int visits = 0;
        int logBudget = 200;

        // Seed with resolver objects + every entity.
        var roots = new List<object>();
        try
        {
            var fiResolved = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
            if (fiResolved?.GetValue(resolver) is System.Collections.IEnumerable resolved)
                foreach (var o in resolved) if (o is not null) roots.Add(o);
        }
        catch { }

        var tEntitiesManager = AssemblyLoader.FindType("Mafi.Core.Entities.EntitiesManager");
        if (tEntitiesManager is not null)
        {
            var em = roots.FirstOrDefault(o => tEntitiesManager.IsAssignableFrom(o.GetType()));
            if (em is not null)
            {
                var fiLinear = FindFieldDeep(em.GetType(), "m_entitiesLinear");
                if (fiLinear?.GetValue(em) is System.Collections.IEnumerable entities)
                    foreach (var e in entities) if (e is not null) roots.Add(e);
            }
        }

        progress?.Report($"  Catch-all phantom-ref nuller: {roots.Count} root(s), {_phantomProtoStubs.Count} phantom stub(s) to kill.");

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue   = new Queue<object>();
        foreach (var r in roots) if (visited.Add(r)) queue.Enqueue(r);

        while (queue.Count > 0 && visits < VisitBudget)
        {
            var obj = queue.Dequeue();
            visits++;
            var objType = obj.GetType();

            for (var cur = objType; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo[] fields;
                try { fields = cur.GetFields(allFlags | BindingFlags.DeclaredOnly); }
                catch { continue; }

                foreach (var fi in fields)
                {
                    var ft = fi.FieldType;

                    // Direct reference field holding a phantom stub: null it.
                    if (!ft.IsValueType && ft != typeof(string))
                    {
                        object? val;
                        try { val = fi.GetValue(obj); }
                        catch { continue; }
                        if (val is null) continue;

                        if (_phantomProtoStubs.Contains(val))
                        {
                            try
                            {
                                fi.SetValue(obj, null);
                                nulled++;
                                if (logBudget-- > 0)
                                    progress?.Report($"    Nulled phantom ref {objType.Name}.{fi.Name}");
                            }
                            catch { }
                            continue;
                        }

                        // Enqueue for further BFS — but skip strings, primitives, and
                        // all collections (handled separately below to avoid lazy-enumerator hangs).
                        if (val is System.Collections.IEnumerable) continue;
                        if (visited.Add(val) && queue.Count < QueueSizeCap) queue.Enqueue(val);
                        continue;
                    }

                    // Option<T> where T is a reference type: clear if held inner is a phantom.
                    if (ft.IsValueType && ft.IsGenericType
                        && ft.Name.StartsWith("Option`", StringComparison.Ordinal))
                    {
                        var innerType = ft.GetGenericArguments()[0];
                        if (innerType.IsValueType) continue;
                        object? optBox;
                        try { optBox = fi.GetValue(obj); }
                        catch { continue; }
                        if (optBox is null) continue;

                        bool clear = false;
                        foreach (var optFi in ft.GetFields(allFlags))
                        {
                            if (optFi.FieldType.IsValueType) continue;
                            if (!innerType.IsAssignableFrom(optFi.FieldType)) continue;
                            try
                            {
                                var inner = optFi.GetValue(optBox);
                                if (inner is not null && _phantomProtoStubs.Contains(inner))
                                {
                                    clear = true;
                                    break;
                                }
                            }
                            catch { }
                        }
                        if (clear)
                        {
                            var noneField = ft.GetField("None", BindingFlags.Public | BindingFlags.Static);
                            if (noneField is not null)
                            {
                                try
                                {
                                    fi.SetValue(obj, noneField.GetValue(null));
                                    nulled++;
                                    if (logBudget-- > 0)
                                        progress?.Report($"    Cleared phantom Option<{innerType.Name}> at {objType.Name}.{fi.Name}");
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
        }

        if (visits >= VisitBudget)
            progress?.Report($"  Catch-all phantom-ref nuller hit visit budget {VisitBudget:N0}; some refs may remain.");
        progress?.Report($"  Catch-all phantom-ref nuller: {nulled} reference(s) nulled across {visits:N0} object(s).");
    }

    // ── Removed-mod entity dangling-reference kill pass ─────────────────────
    //
    // After stripped entities are removed from the resolver, vanilla objects (e.g.
    // IoPort) may still hold direct reference fields pointing to the now-stripped
    // mod entities. When BlobWriter serialises the vanilla object it follows those
    // references and writes the mod entity's AQN — which the game cannot load.
    //
    // This pass walks the same BFS as NullAllPhantomStubReferences but nulls any
    // reference field whose *runtime* type belongs to a stripped assembly, rather
    // than checking for membership in _phantomProtoStubs.
    //
    // Run as Step 5f, immediately after the phantom-stub null pass (Step 5e) and
    // before re-serialisation.

    /// <summary>
    /// BFS over resolver objects + entities; nulls every reference field whose held
    /// value's runtime type is in <paramref name="stripAssemblies"/>. Catches dangling
    /// references from vanilla entities to already-stripped mod entities (e.g.
    /// IoPort.m_connected pointing at a SmartZipper or FishFarm that was stripped).
    /// </summary>
    private void NullAllRemovedModEntityReferences(
        object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        if (stripAssemblies.Count == 0)
        {
            progress?.Report("  No strip assemblies — skipping removed-mod entity ref pass.");
            return;
        }

        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        int nulled = 0;
        int visits = 0;
        int logBudget = 100;

        // Seed: resolver objects + all entities (same scope as phantom-stub pass).
        // ── Collect roots: resolver objects + all entities ────────────────────────────
        // The full object graph is ~167k objects (confirmed by the catch-all phantom-nuller).
        // We run to completion with no budget so every reachable ref is checked.
        var roots = new List<object>();
        try
        {
            var fiResolved = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
            if (fiResolved?.GetValue(resolver) is System.Collections.IEnumerable resolved)
                foreach (var o in resolved) if (o is not null) roots.Add(o);
        }
        catch { }

        // BlobWriter serialises the WHOLE resolver — that includes m_resolvedObjects
        // (Lyst), m_resolvedInstancesByRealType (Dict<Type,object>), AND
        // m_resolvedInstancesByRegisteredType (Dict<Type,object>). Any orphan
        // mod-entity instance reachable only through one of those dicts (e.g. a
        // CargoShipDrydock registered under `IDrydockManager` but whose owning
        // DrydockManager has already been stripped) is otherwise invisible to a BFS
        // that seeds only from m_resolvedObjects, so we seed from those dicts too.
        SeedRootsFromResolverDict(resolver, "m_resolvedInstancesByRealType", roots);
        SeedRootsFromResolverDict(resolver, "m_resolvedInstancesByRegisteredType", roots);

        var tEntitiesManager = AssemblyLoader.FindType("Mafi.Core.Entities.EntitiesManager");
        if (tEntitiesManager is not null)
        {
            var em = roots.FirstOrDefault(o => tEntitiesManager.IsAssignableFrom(o.GetType()));
            if (em is not null)
            {
                var fiLinear = FindFieldDeep(em.GetType(), "m_entitiesLinear");
                if (fiLinear?.GetValue(em) is System.Collections.IEnumerable entities)
                    foreach (var e in entities) if (e is not null) roots.Add(e);
            }
        }

        progress?.Report($"  Removed-mod entity ref nuller: {roots.Count} root(s), scanning for refs into [{string.Join(", ", stripAssemblies)}].");

        const int VisitBudget  = 5_000_000;
        const int QueueSizeCap = 2_000_000;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue   = new Queue<object>();
        foreach (var r in roots)
        {
            // Skip mod-assembly roots: they're being stripped, no need to clean their fields.
            if (!ShouldStrip(r.GetType(), stripAssemblies) && visited.Add(r))
                queue.Enqueue(r);
        }

        int lastProgressReport = 0;
        while (queue.Count > 0 && visits < VisitBudget)
        {
            if (visits - lastProgressReport >= 100_000)
            {
                progress?.Report($"  [entity-ref-nuller] {visits:N0} objects scanned, queue={queue.Count:N0}, nulled={nulled}…");
                lastProgressReport = visits;
            }
            var obj = queue.Dequeue();
            visits++;
            var objType = obj.GetType();

            for (var cur = objType; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo[] fields;
                try { fields = cur.GetFields(allFlags | BindingFlags.DeclaredOnly); }
                catch { continue; }

                foreach (var fi in fields)
                {
                    var ft = fi.FieldType;
                    if (ft == typeof(string)) continue;
                    bool ftIsEnumerable = typeof(System.Collections.IEnumerable).IsAssignableFrom(ft);

                    // Non-IEnumerable value-type (struct) field: box it and scan its
                    // class-type sub-fields for stripped references. This handles e.g.
                    // Option<IoPort> where IoPort.OwnerEntity → SmartZipper — a struct-
                    // then-class path the BFS would otherwise never reach.
                    if (ft.IsValueType && !ftIsEnumerable)
                    {
                        object? boxed;
                        try { boxed = fi.GetValue(obj); } catch { continue; }
                        if (boxed is null) continue;
                        foreach (var sfi in ft.GetFields(allFlags | BindingFlags.DeclaredOnly))
                        {
                            if (sfi.FieldType.IsValueType || sfi.FieldType == typeof(string)) continue;
                            object? sv;
                            try { sv = sfi.GetValue(boxed); } catch { continue; }
                            if (sv is null) continue;
                            var svType = sv.GetType();
                            if (svType.IsValueType) continue;
                            if (ShouldStrip(svType, stripAssemblies))
                            {
                                // Direct stripped ref inside struct — null it and write back.
                                try
                                {
                                    sfi.SetValue(boxed, null);
                                    fi.SetValue(obj, boxed);
                                    nulled++;
                                    if (logBudget-- > 0)
                                        progress?.Report($"    Nulled struct-ref {objType.Name}.{fi.Name}.{sfi.Name} → {svType.FullName}");
                                }
                                catch { }
                            }
                            else if (visited.Add(sv) && queue.Count < QueueSizeCap)
                            {
                                queue.Enqueue(sv); // class ref inside struct — go deeper
                            }
                        }
                        continue;
                    }

                    object? val;
                    try { val = fi.GetValue(obj); }
                    catch { continue; }
                    if (val is null) continue;

                    var valType = val.GetType();

                    // Boxed value type that isn't an IEnumerable — nothing useful to do.
                    if (valType.IsValueType && val is not System.Collections.IEnumerable) continue;

                    if (!valType.IsValueType && ShouldStrip(valType, stripAssemblies))
                    {
                        try
                        {
                            fi.SetValue(obj, null);
                            nulled++;
                            if (logBudget-- > 0)
                                progress?.Report($"    Nulled removed-mod ref {objType.Name}.{fi.Name} → {valType.FullName}");
                        }
                        catch { }
                        continue; // don't enqueue the stripped value
                    }

                    // For collections, traverse elements. Any stripped elements are
                    // removed from the collection (so BlobWriter never writes their AQN).
                    if (val is System.Collections.IEnumerable enumerable)
                    {
                        try
                        {
                            // Scan first so we know whether removal is needed.
                            var strippedElems = new List<object>();
                            const BindingFlags structFieldFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                            foreach (var elem in enumerable)
                            {
                                if (elem is null) continue;
                                var elemType = elem.GetType();
                                if (elemType == typeof(string)) continue;
                                if (elemType.IsValueType)
                                {
                                    // Struct element (e.g. KeyValuePair<K,V> from Dict<K,V>):
                                    // the BFS can't enqueue a struct, but its reference-type
                                    // sub-fields may hold class instances (e.g. the V in
                                    // Dict<ProductProto, RegisteredBuffers>) that contain
                                    // stripped refs deeper in the graph.
                                    foreach (var sfi in elemType.GetFields(structFieldFlags))
                                    {
                                        if (sfi.FieldType.IsValueType || sfi.FieldType == typeof(string)) continue;
                                        object? sv;
                                        try { sv = sfi.GetValue(elem); } catch { continue; }
                                        if (sv is null) continue;
                                        var svType = sv.GetType();
                                        if (svType.IsValueType) continue;
                                        if (!ShouldStrip(svType, stripAssemblies) && visited.Add(sv) && queue.Count < QueueSizeCap)
                                            queue.Enqueue(sv);
                                    }
                                    continue;
                                }
                                if (ShouldStrip(elemType, stripAssemblies))
                                {
                                    strippedElems.Add(elem);
                                    if (logBudget-- > 0)
                                        progress?.Report($"    [COLL-HIT] {objType.Name}.{fi.Name} ({ft.Name}) contains {elemType.FullName}");
                                }
                                else
                                {
                                    if (visited.Add(elem) && queue.Count < QueueSizeCap) queue.Enqueue(elem);
                                }
                            }

                            // STRUCT-IN-COLLECTION: Lyst<T> / array where T is a struct
                            // is invisible to the foreach above (we boxed and skipped).
                            // Walk the underlying m_items array (Lyst<T>) directly and
                            // null any ref-type sub-field of a struct slot whose value
                            // is in a removed-mod assembly. Catches e.g. Lyst<Entry>
                            // where Entry { Entity Owner; … } and Owner == stripped.
                            ScrubStrippedRefsInStructLystSlots(val, stripAssemblies, ref nulled, ref logBudget, progress);

                            if (strippedElems.Count > 0)
                            {
                                // Try to remove each stripped element from the collection.
                                var removeMethod = val.GetType().GetMethods()
                                    .FirstOrDefault(m => m.Name == "Remove"
                                        && m.GetParameters().Length == 1
                                        && !m.GetParameters()[0].ParameterType.IsValueType);
                                if (removeMethod != null)
                                {
                                    foreach (var se in strippedElems)
                                        try { removeMethod.Invoke(val, new[] { se }); nulled++; } catch { }
                                }
                                else
                                {
                                    // Collection is immutable (ImmutableArray) or has no Remove:
                                    // rebuild it via the backing array and replace the field.
                                    var fiItems = val.GetType().GetField("m_items",
                                        BindingFlags.NonPublic | BindingFlags.Instance);
                                    var fiSize  = val.GetType().GetField("m_size",
                                        BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (fiItems != null && fiSize != null)
                                    {
                                        var arr  = fiItems.GetValue(val) as Array;
                                        var size = (int)(fiSize.GetValue(val) ?? 0);
                                        if (arr != null)
                                        {
                                            var stripped = new HashSet<object>(strippedElems, ReferenceEqualityComparer.Instance);
                                            int write = 0;
                                            for (int ai = 0; ai < size; ai++)
                                            {
                                                var it = arr.GetValue(ai);
                                                if (it != null && stripped.Contains(it)) { nulled++; continue; }
                                                arr.SetValue(it, write++);
                                            }
                                            for (int ai = write; ai < size; ai++) arr.SetValue(null, ai);
                                            fiSize.SetValue(val, write);
                                        }
                                    }
                                    else
                                    {
                                        // Last resort: null the whole field.
                                        try { fi.SetValue(obj, null); nulled++; } catch { }
                                    }
                                }
                            }
                        }
                        catch { }
                        continue;
                    }

                    if (visited.Add(val) && queue.Count < QueueSizeCap) queue.Enqueue(val);
                }
            }
        }

        bool budgetHit = visits >= VisitBudget;
        progress?.Report($"  Removed-mod entity ref nuller: {nulled} reference(s) nulled across {visits:N0} object(s){(budgetHit ? " (visit budget hit)" : "")}.");
    }

    // ── Dict<entity, V> key scrub ─────────────────────────────────────────────
    //
    // The BFS in NullAllRemovedModEntityReferences nulls reference-type fields and
    // removes stripped elements from IEnumerable collections. However, it cannot
    // remove KEYS from Dict<K,V> when K is a reference type (interface or class),
    // because iterating the dict yields KeyValuePair<K,V> VALUE TYPES — and the BFS
    // skips all value-type elements. Those surviving stripped-entity keys are then
    // written by BlobWriter with their mod-assembly AQN; the AQN patcher rewrites
    // them to System.Object, which passes validation but causes the game to crash on
    // load ("Failed to deserialize type 'Object' … not assignable to IStaticEntity").
    //
    // Known victim: VehicleBuffersRegistry.m_registeredBuffersPerEntity:
    //   Dict<IStaticEntity, RegisteredBuffersPerEntity>
    //
    // This pass scans every resolver object for Mafi Dict<K,V> fields where K is a
    // non-proto reference type, and removes any entry whose key's concrete runtime
    // type belongs to a stripped assembly.

    // ── Broad-object-collection helper shared by the purge passes ─────────────────────
    // The BFS seeds from three resolver collections; the old per-manager purge only used
    // m_resolvedObjects and silently missed managers registered only in the type-keyed
    // dicts. This helper replicates the BFS seed strategy so purges find the same objects.
    private List<object> CollectAllResolverObjects(object resolver)
    {
        var all = new List<object>();
        try
        {
            var fiResolved = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
            if (fiResolved?.GetValue(resolver) is System.Collections.IEnumerable resolved)
                foreach (var o in resolved) if (o is not null) all.Add(o);
        }
        catch { }
        SeedRootsFromResolverDict(resolver, "m_resolvedInstancesByRealType", all);
        SeedRootsFromResolverDict(resolver, "m_resolvedInstancesByRegisteredType", all);
        return all;
    }

    /// <summary>
    /// Compacts a <c>Lyst&lt;T&gt;</c> (class-backed) by removing items where a nominated
    /// entity-like reference field is null or belongs to a stripped assembly.
    /// </summary>
    private int PurgeLystByField(object lystObj, FieldInfo fiField,
        HashSet<string> stripAssemblies, bool checkStrip, out int found)
    {
        found = 0;
        if (lystObj is null || fiField is null) return 0;
        var lystType = lystObj.GetType();
        var fiItems  = lystType.GetField("m_items", BindingFlags.NonPublic | BindingFlags.Instance);
        var fiSize   = lystType.GetField("m_size",  BindingFlags.NonPublic | BindingFlags.Instance);
        if (fiItems is null || fiSize is null) return 0;
        var items = fiItems.GetValue(lystObj) as Array;
        var size  = (int)(fiSize.GetValue(lystObj) ?? 0);
        if (items is null || size == 0) return 0;
        found = size;
        int purged = 0, write = 0;
        for (int i = 0; i < size; i++)
        {
            var item = items.GetValue(i);
            if (item is null) { purged++; continue; }
            object? fieldVal;
            try { fieldVal = fiField.GetValue(item); } catch { fieldVal = null; }
            bool bad = fieldVal is null
                || (checkStrip && ShouldStrip(fieldVal.GetType(), stripAssemblies));
            if (bad) { purged++; continue; }
            items.SetValue(item, write++);
        }
        for (int i = write; i < size; i++) items.SetValue(null, i);
        fiSize.SetValue(lystObj, write);
        return purged;
    }

    /// <summary>
    /// Compacts a <c>LystStruct&lt;T&gt;</c> (value-type-element list; may itself be a struct
    /// stored in a manager field) by removing entries where a nominated proto-like field
    /// is null or from a stripped assembly. Handles value-type lyst fields by writing back.
    /// <para>
    /// LystStruct&lt;T&gt; internals (Mafi 0.8.x): <c>private T[] m_items</c> and
    /// <c>public int Count { get; private set; }</c> — Count's backing field is the
    /// compiler-generated <c>&lt;Count&gt;k__BackingField</c>, NOT "m_size".
    /// </para>
    /// </summary>
    private int PurgeLystStructByProtoField(object manager, string lystFieldName,
        string protoFieldName, HashSet<string> stripAssemblies,
        IProgress<string>? progress)
    {
        try
        {
            var fiLyst = FindFieldDeep(manager.GetType(), lystFieldName);
            if (fiLyst is null) { progress?.Report($"    [lyst-purge] {manager.GetType().Name}.{lystFieldName} field not found."); return 0; }
            var lystVal  = fiLyst.GetValue(manager);
            if (lystVal is null) return 0;
            var lystType = lystVal.GetType();
            var fiItems  = lystType.GetField("m_items", BindingFlags.NonPublic | BindingFlags.Instance);
            // LystStruct uses "public int Count { get; private set; }" — backing field is
            // "<Count>k__BackingField", not "m_size".
            var fiCount  = lystType.GetField("<Count>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? lystType.GetField("m_size", BindingFlags.NonPublic | BindingFlags.Instance);
            if (fiItems is null) { progress?.Report($"    [lyst-purge] {lystType.Name}.m_items not found."); return 0; }
            if (fiCount is null) { progress?.Report($"    [lyst-purge] {lystType.Name} Count/m_size backing field not found."); return 0; }
            var items = fiItems.GetValue(lystVal) as Array;
            var size  = (int)(fiCount.GetValue(lystVal) ?? 0);
            if (items is null || size == 0) return 0;

            var elemType = items.GetType().GetElementType();
            if (elemType is null) return 0;
            var fiProto = FindFieldDeep(elemType, protoFieldName);
            if (fiProto is null) { progress?.Report($"    [lyst-purge] {elemType.Name}.{protoFieldName} field not found."); return 0; }

            int purged = 0, write = 0;
            for (int i = 0; i < size; i++)
            {
                var entry = items.GetValue(i);
                if (entry is null) { purged++; continue; }
                object? proto;
                try { proto = fiProto.GetValue(entry); } catch { proto = null; }
                bool bad = proto is null || ShouldStrip(proto.GetType(), stripAssemblies);
                if (bad) { purged++; continue; }
                if (i != write) items.SetValue(entry, write);
                write++;
            }
            if (purged > 0)
            {
                // Zero tail slots with a default-constructed element.
                try
                {
                    var dflt = Activator.CreateInstance(elemType);
                    for (int i = write; i < size; i++) items.SetValue(dflt, i);
                }
                catch { }
                fiCount.SetValue(lystVal, write);
                // LystStruct IS a value type — the boxed copy must be written back.
                if (lystType.IsValueType) fiLyst.SetValue(manager, lystVal);
                progress?.Report($"    [{manager.GetType().Name}.{lystFieldName}] Purged {purged} null/stripped-proto entries.");
            }
            return purged;
        }
        catch (Exception ex)
        {
            progress?.Report($"    [lyst-purge {lystFieldName}] Exception: {ex.Message}");
            return 0;
        }
    }

    /// Returns any vanilla proto from <c>_protoHealingLookup</c> that is assignable to
    /// <paramref name="fieldType"/>. Used to assign a fallback proto to vanilla entities
    /// whose COIExtended proto was nulled by the phantom pass, preventing NullReferenceException
    /// in WorkersManager.initSelf when it calls Entity.WorkersNeeded.
    private object? FindAnyVanillaProto(Type fieldType)
    {
        if (_protoHealingLookup is null || fieldType is null || fieldType.IsInterface) return null;
        if (_protoHealingLookup.ByExactType.TryGetValue(fieldType, out var exact) && exact.Count > 0)
            return exact[0];
        if (_protoHealingLookup.ByAssignableType.TryGetValue(fieldType, out var assignable) && assignable.Count > 0)
            return assignable.FirstOrDefault(p => fieldType.IsInstanceOfType(p));
        return null;
    }

    /// <summary>
    /// Removes <c>EntityWorkersAssigner</c> entries from <c>WorkersManager.m_sortedAssigners</c>
    /// whose Entity is null, from a stripped assembly, or has a stripped-assembly proto.
    /// Also purges null-proto entries from <c>ElectricityManager</c> and
    /// <c>MaintenanceManager.Buffer</c> stats lists to prevent duplicate-key crashes in
    /// their Phase 3 <c>initSelf</c> calls.
    /// </summary>
    private void PurgeNullEntityWorkerAssigners(
        object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        try
        {
            // ── collect objects from ALL resolver sources (same scope as the BFS) ───────
            // Previous version only searched m_resolvedObjects; WorkersManager and other
            // managers are registered in m_resolvedInstancesByRealType /
            // m_resolvedInstancesByRegisteredType and were silently missed → "Purged 0".
            var allObjects = CollectAllResolverObjects(resolver);

            int totalPurged = 0;

            // ── WorkersManager.m_sortedAssigners ────────────────────────────────────────
            var tWorkersManager = AssemblyLoader.FindType("Mafi.Core.Population.WorkersManager");
            var tEntityWorkersAssigner = AssemblyLoader.FindType("Mafi.Core.Population.WorkersManager+EntityWorkersAssigner");
            if (tWorkersManager is not null && tEntityWorkersAssigner is not null)
            {
                int mgrsFound = 0;
                foreach (var obj in allObjects)
                {
                    if (obj is null || !tWorkersManager.IsAssignableFrom(obj.GetType())) continue;
                    mgrsFound++;

                    var fiAssigners = FindFieldDeep(obj.GetType(), "m_sortedAssigners");
                    if (fiAssigners is null) continue;
                    var assignersList = fiAssigners.GetValue(obj);
                    if (assignersList is null) continue;

                    var assignersType = assignersList.GetType();
                    var fiItems = assignersType.GetField("m_items", BindingFlags.NonPublic | BindingFlags.Instance);
                    var fiSize  = assignersType.GetField("m_size",  BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fiItems is null || fiSize is null) continue;

                    var items = fiItems.GetValue(assignersList) as Array;
                    var size  = (int)(fiSize.GetValue(assignersList) ?? 0);
                    progress?.Report($"    [worker-assigner-purge] m_sortedAssigners size={size}, fiEntity={(FindFieldDeep(tEntityWorkersAssigner, "Entity") is null ? "NULL" : "ok")}");
                    if (items is null || size == 0) continue;

                    var fiEntity   = FindFieldDeep(tEntityWorkersAssigner, "Entity");
                    if (fiEntity is null) { progress?.Report("    [worker-assigner-purge] Entity field not found!"); continue; }

                    // Sample up to 5 entity types for diagnostics.
                    var sampleTypes = new System.Collections.Generic.HashSet<string>();
                    int write = 0;
                    for (int i = 0; i < size; i++)
                    {
                        var assigner = items.GetValue(i);
                        if (assigner is null) { totalPurged++; continue; }   // was: write++ (bug)
                        var entity = fiEntity.GetValue(assigner);
                        if (entity is null) { totalPurged++; continue; }
                        if (sampleTypes.Count < 5) sampleTypes.Add(entity.GetType().FullName ?? "?");
                        // Strip if entity type is from a stripped assembly, or if the entity's
                        // proto was nulled by the phantom-proto pass (meaning it was a
                        // COIExtended proto — e.g. a vanilla Truck upgraded to a COIExtended
                        // VehicleMaker proto, which got nulled when we stripped it).
                        bool stripByEntityType = ShouldStrip(entity.GetType(), stripAssemblies);
                        bool stripByProtoType  = false;
                        if (!stripByEntityType)
                        {
                            var fiProtoField = FindFieldDeep(entity.GetType(), "Prototype")
                                            ?? FindFieldDeep(entity.GetType(), "Proto");
                            if (fiProtoField is not null)
                            {
                                var protoVal = fiProtoField.GetValue(entity);
                                if (protoVal is not null && ShouldStrip(protoVal.GetType(), stripAssemblies))
                                {
                                    // Proto is still a live COIExtended type — serializer can't write it.
                                    stripByProtoType = true;
                                }
                                else if (protoVal is null)
                                {
                                    // Proto was nulled by the phantom pass (was a COIExtended proto).
                                    // Assign any vanilla proto of the correct type so WorkersManager.initSelf
                                    // can call Entity.WorkersNeeded without NullReferenceException.
                                    var fallback = FindAnyVanillaProto(fiProtoField.FieldType);
                                    if (fallback is not null)
                                        fiProtoField.SetValue(entity, fallback);
                                    else
                                        stripByProtoType = true;
                                }
                            }
                        }
                        if (stripByEntityType || stripByProtoType) { totalPurged++; continue; }
                        items.SetValue(assigner, write++);
                    }
                    for (int i = write; i < size; i++) items.SetValue(null, i);
                    fiSize.SetValue(assignersList, write);
                    if (sampleTypes.Count > 0)
                        progress?.Report($"    [worker-assigner-purge] Sample entity types: {string.Join(", ", sampleTypes)}");
                }
                progress?.Report($"  [worker-assigner-purge] Found {mgrsFound} WorkersManager(s). Purged {totalPurged} assigner(s).");
            }

            // ── ElectricityManager stats lists ───────────────────────────────────────────
            // m_consumptionStatsPerProto / m_productionStatsPerProto are LystStruct<T>
            // where T.ConsumerProto/ProducerProto is an IEntityProto. After the phantom
            // proto pass, stripped-mod protos in these structs become null. When the game
            // runs ElectricityManager.initSelf it builds m_consumerProtoIdsMap via
            // Dict.Add(proto, index); duplicate null keys → ArgumentException.
            var tElec = AssemblyLoader.FindType("Mafi.Core.Factory.ElectricPower.ElectricityManager");
            if (tElec is not null)
            {
                int elecMgrsFound = 0;
                foreach (var obj in allObjects)
                {
                    if (obj is null || !tElec.IsAssignableFrom(obj.GetType())) continue;
                    elecMgrsFound++;
                    int p1 = PurgeLystStructByProtoField(obj, "m_consumptionStatsPerProto", "ConsumerProto", stripAssemblies, progress);
                    int p2 = PurgeLystStructByProtoField(obj, "m_productionStatsPerProto",  "ProducerProto",  stripAssemblies, progress);
                    totalPurged += p1 + p2;
                }
                progress?.Report($"  [elec-stats-purge] Found {elecMgrsFound} ElectricityManager(s), purged {totalPurged} stat entries total.");
            }

            // ── MaintenanceManager.Buffer stats lists ────────────────────────────────────
            // Buffer instances are NOT top-level resolver objects; they live inside
            // MaintenanceManager.m_buffers (Dict<ProductProto, Buffer>).
            // Walk the dict values to reach each Buffer and purge its ConsumptionStatsPerProto.
            var tMaintenanceMgr = AssemblyLoader.FindType("Mafi.Core.Maintenance.MaintenanceManager");
            var tMaintBuf       = AssemblyLoader.FindType("Mafi.Core.Maintenance.MaintenanceManager+Buffer");
            if (tMaintenanceMgr is not null && tMaintBuf is not null)
            {
                int maintFound = 0, maintPurged = 0;
                foreach (var obj in allObjects)
                {
                    if (obj is null || !tMaintenanceMgr.IsAssignableFrom(obj.GetType())) continue;
                    var fiBuffers = FindFieldDeep(obj.GetType(), "m_buffers");
                    if (fiBuffers is null) continue;
                    var buffersDict = fiBuffers.GetValue(obj);
                    if (buffersDict is not System.Collections.IEnumerable dictEnum) continue;
                    foreach (var kvp in dictEnum)
                    {
                        if (kvp is null) continue;
                        object? bufferObj;
                        try { bufferObj = kvp.GetType().GetProperty("Value")?.GetValue(kvp); }
                        catch { bufferObj = null; }
                        if (bufferObj is null || !tMaintBuf.IsAssignableFrom(bufferObj.GetType())) continue;
                        maintFound++;
                        maintPurged += PurgeLystStructByProtoField(bufferObj, "ConsumptionStatsPerProto", "Proto", stripAssemblies, progress);
                    }
                }
                totalPurged += maintPurged;
                progress?.Report($"  [maint-stats-purge] Found {maintFound} MaintenanceManager.Buffer(s), purged {maintPurged} stat entries.");
            }

            progress?.Report($"  [manager-purge] Total purged across all managers: {totalPurged}.");
        }
        catch (Exception ex)
        {
            progress?.Report($"  [manager-purge] Exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes entries from Mafi <c>Dict&lt;K,V&gt;</c> fields on resolver objects where
    /// the key's concrete runtime type is from a stripped-mod assembly. Handles cases
    /// like <c>VehicleBuffersRegistry.m_registeredBuffersPerEntity</c> where the BFS
    /// cannot reach the keys because <c>KeyValuePair&lt;K,V&gt;</c> is a value type.
    /// </summary>
    private void ScrubStrippedEntityKeysFromManagerDicts(
        object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        if (stripAssemblies.Count == 0)
        {
            progress?.Report("  [dict-key-scrub] No strip assemblies — skipping.");
            return;
        }

        var tProto = AssemblyLoader.FindType("Mafi.Core.Prototypes.Proto");
        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        int totalRemoved = 0;
        int dictsScanned = 0;

        // Collect all resolver objects to scan — mirror the seed strategy used by
        // NullAllRemovedModEntityReferences so we find managers like VehicleBuffersRegistry
        // that are registered AsEverything and live in the type-keyed instance dicts,
        // not necessarily in m_resolvedObjects.
        var objectsToScan = new HashSet<object>(ReferenceEqualityComparer.Instance);
        try
        {
            var fiResolved = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
            if (fiResolved?.GetValue(resolver) is System.Collections.IEnumerable resolved)
                foreach (var o in resolved) if (o is not null) objectsToScan.Add(o);
        }
        catch { }

        void SeedFromResolverDict(string dictFieldName)
        {
            try
            {
                var fi = FindFieldDeep(resolver.GetType(), dictFieldName);
                if (fi?.GetValue(resolver) is not System.Collections.IEnumerable dict) return;
                foreach (var kv in dict)
                {
                    if (kv is null) continue;
                    var valProp = kv.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    if (valProp?.GetValue(kv) is object v) objectsToScan.Add(v);
                }
            }
            catch { }
        }
        SeedFromResolverDict("m_resolvedInstancesByRealType");
        SeedFromResolverDict("m_resolvedInstancesByRegisteredType");

        foreach (var obj in objectsToScan)
        {
            if (obj is null) continue;
            var objType = obj.GetType();
            if (ShouldStrip(objType, stripAssemblies)) continue;

            for (var cur = objType; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo[] fields;
                try { fields = cur.GetFields(allFlags | BindingFlags.DeclaredOnly); }
                catch { continue; }

                foreach (var fi in fields)
                {
                    var ft = fi.FieldType;
                    if (ft.IsValueType) continue;
                    if (!ft.IsGenericType) continue;

                    var typeArgs = ft.GetGenericArguments();
                    if (typeArgs.Length < 2) continue;
                    var keyType = typeArgs[0];

                    // Skip dicts whose key is a value type — those can never be mod types.
                    // We intentionally do NOT skip proto-keyed dicts: the VALUES may still be
                    // stripped mod entities (e.g. Dict<ProductProto, IVirtualBufferProvider>
                    // where the value is a mod entity implementing that interface).
                    if (keyType.IsValueType) continue;

                    object? dictVal;
                    try { dictVal = fi.GetValue(obj); }
                    catch { continue; }
                    if (dictVal is null) continue;

                    var dictType = dictVal.GetType();

                    // Read the dict's m_entries array directly — bypasses GetEnumerator() which
                    // throws "not initialized after load" if m_buckets hasn't been rebuilt yet.
                    //
                    // Mafi Dict has two states when we see it:
                    //  A) initAfterLoad HAS run (m_buckets != null):
                    //       m_count = used slots, HashCode >= 0 → live entry.
                    //  B) initAfterLoad NOT yet run (m_buckets == null, m_entries != null):
                    //       DeserializeData built m_entries[0..num-1] with HashCode=0;
                    //       m_count is still the default 0.
                    //       Iterate m_entries.Length slots; treat all non-null keys as live.
                    var fiEntries  = FindFieldDeep(dictType, "m_entries");
                    var fiCount    = FindFieldDeep(dictType, "m_count");
                    var fiBuckets  = FindFieldDeep(dictType, "m_buckets");
                    if (fiEntries is null || fiCount is null) continue;

                    Array? entries;
                    int    mCount;
                    bool   bucketsBuilt;
                    try
                    {
                        entries      = fiEntries.GetValue(dictVal) as Array;
                        mCount       = (int)(fiCount.GetValue(dictVal) ?? 0);
                        bucketsBuilt = fiBuckets != null && fiBuckets.GetValue(dictVal) != null;
                    }
                    catch { continue; }

                    // If initAfterLoad ran: iterate [0, m_count); live = HashCode >= 0.
                    // If NOT ran: iterate all of m_entries; all non-null keys are live
                    //   (DeserializeData sets HashCode = 0 for every entry it writes).
                    int iterLimit = bucketsBuilt ? mCount : (entries?.Length ?? 0);
                    if (entries is null || iterLimit == 0) continue;

                    // Each element is an Entry struct { HashCode, NextEntryIndex, Key, Value }.
                    // State A (initAfterLoad ran):    HashCode >= 0 → live,  < 0 → deleted.
                    // State B (initAfterLoad NOT ran): every entry is live (HashCode = 0,
                    //    set by DeserializeData's `new Entry(0,0,key,value)`).
                    try
                    {
                        var entryType  = entries.GetType().GetElementType();
                        if (entryType is null) continue;
                        var fiHashCode   = entryType.GetField("HashCode",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var fiEntryKey   = entryType.GetField("Key",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var fiEntryValue = entryType.GetField("Value",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (fiHashCode is null || fiEntryKey is null) continue;

                        bool EntryIsStripped(object entry)
                        {
                            var k = fiEntryKey.GetValue(entry);
                            if (k is not null && ShouldStrip(k.GetType(), stripAssemblies)) return true;
                            if (fiEntryValue is not null)
                            {
                                var v = fiEntryValue.GetValue(entry);
                                if (v is not null && ShouldStrip(v.GetType(), stripAssemblies)) return true;
                            }
                            return false;
                        }

                        if (bucketsBuilt)
                        {
                            // ── State A: dict is fully initialized — call Remove(key). ──────
                            var keysToRemove = new List<object>();
                            for (int ei = 0; ei < iterLimit; ei++)
                            {
                                var entry = entries.GetValue(ei);
                                if (entry is null) continue;
                                int hc = (int)(fiHashCode.GetValue(entry) ?? -1);
                                if (hc < 0) continue; // deleted/empty slot
                                var key = fiEntryKey.GetValue(entry);
                                if (key is null) continue;
                                if (EntryIsStripped(entry))
                                    keysToRemove.Add(key);
                            }
                            if (keysToRemove.Count == 0) continue;

                            var removeMethod = dictType.GetMethod("Remove", new[] { keyType });
                            if (removeMethod is null) continue;

                            dictsScanned++;
                            int removedFromThis = 0;
                            foreach (var key in keysToRemove)
                            {
                                try { removeMethod.Invoke(dictVal, new[] { key }); removedFromThis++; }
                                catch { }
                            }
                            totalRemoved += removedFromThis;
                            progress?.Report(
                                $"  [dict-scrub] {objType.Name}.{fi.Name}: " +
                                $"removed {removedFromThis}/{keysToRemove.Count} stripped entry/entries.");
                        }
                        else
                        {
                            // ── State B: initAfterLoad NOT yet called (Phase 3 skipped). ────
                            // m_entries is the raw deserialized array; all entries are live.
                            // Remove by rebuilding m_entries WITHOUT the stripped entries.
                            // The game's own initAfterLoad (on save load) will re-index from
                            // the compacted array, so no stripped entries end up in the final dict.
                            int strippedCount = 0;
                            var keptEntries = new System.Collections.ArrayList(iterLimit);
                            for (int ei = 0; ei < iterLimit; ei++)
                            {
                                var entry = entries.GetValue(ei);
                                if (entry is not null && EntryIsStripped(entry))
                                {
                                    strippedCount++;
                                }
                                else
                                {
                                    keptEntries.Add(entry);
                                }
                            }
                            if (strippedCount == 0) continue;

                            // Replace m_entries with the compacted array; leave m_count = 0
                            // (initAfterLoad will recompute it from the new array).
                            var newEntries = Array.CreateInstance(entryType, keptEntries.Count);
                            for (int i = 0; i < keptEntries.Count; i++)
                                newEntries.SetValue(keptEntries[i], i);
                            fiEntries.SetValue(dictVal, newEntries);

                            dictsScanned++;
                            totalRemoved += strippedCount;
                            progress?.Report(
                                $"  [dict-scrub] {objType.Name}.{fi.Name}: " +
                                $"removed {strippedCount} stripped entry/entries (pre-init compaction, {keptEntries.Count} kept).");
                        }
                    }
                    catch { continue; }
                }
            }
        }

        progress?.Report($"  [dict-scrub] Done: {totalRemoved} entry/entries removed across {dictsScanned} dict field(s).");
    }

    /// <summary>
    /// Adds every non-null Value of the dictionary stored in <paramref name="resolver"/>'s
    /// field <paramref name="dictFieldName"/> to <paramref name="roots"/>. The dict is
    /// expected to be <c>Dictionary&lt;Type, object&gt;</c>; we read each KeyValuePair via
    /// reflection so the helper works regardless of the concrete dictionary type.
    /// <para/>
    /// Used to extend the BFS seed set in <see cref="NullAllRemovedModEntityReferences"/>
    /// beyond <c>m_resolvedObjects</c> so dangling refs reachable only via the
    /// registered-instance dicts (e.g. an orphan <c>CargoShipDrydock</c> registered
    /// under <c>IDrydockManager</c>) are still traversed and nulled.
    /// </summary>
    private static void SeedRootsFromResolverDict(object resolver, string dictFieldName, List<object> roots)
    {
        try
        {
            var fi = FindFieldDeep(resolver.GetType(), dictFieldName);
            if (fi?.GetValue(resolver) is not System.Collections.IEnumerable dict) return;

            foreach (var kv in dict)
            {
                if (kv is null) continue;
                var kvType = kv.GetType();
                var valProp = kvType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                if (valProp?.GetValue(kv) is object v) roots.Add(v);
            }
        }
        catch { /* best-effort seeding; silent failure is OK here */ }
    }

    /// <summary>
    /// Walks the entire deserialised resolver graph hunting for raw <see cref="Type"/>
    /// references whose runtime type belongs to a stripped-mod assembly. Any such Type
    /// stored as an instance value (a Type field, a <c>Lyst&lt;Type&gt;</c> element, a
    /// <c>Dict&lt;Type,*&gt;</c> key, or a CallbackSaveData.DeclaringType) is serialised
    /// inline by <c>BlobWriter.WriteType</c> as the type's full AQN — and that AQN
    /// becomes a future <c>CorruptedSaveException</c> on game load.
    /// <para/>
    /// This pass:
    ///  1. BFSes from every resolver root (<c>m_resolvedObjects</c>, the registered-instance
    ///     dicts, <c>m_instancedToBeDisposed</c>, every entity in EntitiesManager).
    ///  2. Walks reference fields, value-type fields (boxed), and IEnumerable contents.
    ///  3. For every visited object: scans every declared field. If a field is typed
    ///     <see cref="Type"/> and its value is a stripped-mod Type → null it out (when
    ///     the field is settable). If a field's value is a <c>Lyst&lt;Type&gt;</c> →
    ///     compact stripped-mod Type entries out of the backing array. If it's a
    ///     <c>Dict&lt;Type,*&gt;</c> → remove every entry whose key is a stripped Type.
    /// </summary>
    private void DropStrippedModTypeReferences(
        object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        if (stripAssemblies.Count == 0) return;

        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var roots = new List<object>();
        try
        {
            var fiResolved = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
            if (fiResolved?.GetValue(resolver) is System.Collections.IEnumerable resolved)
                foreach (var o in resolved) if (o is not null) roots.Add(o);
        }
        catch { }
        SeedRootsFromResolverDict(resolver, "m_resolvedInstancesByRealType", roots);
        SeedRootsFromResolverDict(resolver, "m_resolvedInstancesByRegisteredType", roots);
        try
        {
            var fiDisposed = FindFieldDeep(resolver.GetType(), "m_instancedToBeDisposed");
            if (fiDisposed?.GetValue(resolver) is System.Collections.IEnumerable disposed)
                foreach (var o in disposed) if (o is not null) roots.Add(o);
        }
        catch { }
        // NOTE: entities are intentionally NOT seeded here. Deep18 proved that
        // scanning 840K objects found only 4 Dict<Type,*> entries — all in the
        // resolver's own registration dicts, which are already reachable from the
        // resolver roots above. Adding 130K entities causes the BFS to traverse the
        // entire game object graph, consuming 20+ GB with a populated resolver and
        // never finding any additional stripped-mod Type refs in entity fields.
        progress?.Report($"  Stripped-mod Type-reference scrub: BFS from {roots.Count} root(s)…");

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        foreach (var r in roots)
        {
            if (!ShouldStrip(r.GetType(), stripAssemblies) && visited.Add(r))
                queue.Enqueue(r);
        }

        int typeFieldsNulled = 0;
        int lystTypeEntriesDropped = 0;
        int dictTypeEntriesDropped = 0;
        int objectsScanned = 0;
        int logBudget = 50;
        const int VisitBudget = 2_000_000;
        const int QueueSizeCap = 600_000;
        var bfsSw = System.Diagnostics.Stopwatch.StartNew();
        int lastReportedCount = 0;

        while (queue.Count > 0 && objectsScanned < VisitBudget)
        {
            var obj = queue.Dequeue();
            objectsScanned++;

            // Periodic progress report — the BFS can take minutes on large saves with
            // a populated resolver (many auto-instantiated manager singletons to visit).
            if (objectsScanned - lastReportedCount >= 50_000 || (bfsSw.ElapsedMilliseconds > 10_000 && objectsScanned > lastReportedCount))
            {
                progress?.Report($"  [type-scrub] BFS progress: {objectsScanned:N0} objects scanned, queue={queue.Count:N0}…");
                lastReportedCount = objectsScanned;
                bfsSw.Restart();
            }
            var objType = obj.GetType();

            for (var cur = objType; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo[] fields;
                try { fields = cur.GetFields(allFlags); }
                catch { continue; }

                foreach (var fi in fields)
                {
                    var ft = fi.FieldType;
                    if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) continue;

                    object? val;
                    try { val = fi.GetValue(obj); } catch { continue; }
                    if (val is null) continue;

                    // (1) Direct Type-typed field.
                    if (val is Type t)
                    {
                        if (ShouldStrip(t, stripAssemblies))
                        {
                            try
                            {
                                fi.SetValue(obj, null);
                                typeFieldsNulled++;
                                if (logBudget-- > 0)
                                    progress?.Report($"    [type-null] {cur.Name}.{fi.Name} → {t.FullName}");
                            }
                            catch { }
                        }
                        continue;
                    }

                    // (2) Lyst<Type> / List<Type>: compact stripped-mod Type entries.
                    if (val is System.Collections.IList listVal)
                    {
                        var vt = val.GetType();
                        if (vt.IsGenericType)
                        {
                            var ga = vt.GetGenericArguments();
                            if (ga.Length == 1 && typeof(Type).IsAssignableFrom(ga[0]))
                            {
                                int dropped = CompactTypeListInPlace(val, vt, stripAssemblies);
                                if (dropped > 0)
                                {
                                    lystTypeEntriesDropped += dropped;
                                    if (logBudget-- > 0)
                                        progress?.Report($"    [lyst-type-drop] {cur.Name}.{fi.Name} dropped {dropped} stripped-mod Type entry(ies)");
                                }
                            }
                        }
                    }

                    // (3) Dict<Type,*>: remove entries with stripped-mod Type keys.
                    // Handle both standard IDictionary and Mafi's custom Dict<T,V>
                    // (which does NOT implement IDictionary).
                    {
                        var vt = val.GetType();
                        if (vt.IsGenericType)
                        {
                            var ga = vt.GetGenericArguments();
                            if (ga.Length == 2 && typeof(Type).IsAssignableFrom(ga[0]))
                            {
                                int dropped;
                                if (val is System.Collections.IDictionary dictVal)
                                    dropped = RemoveStrippedTypeKeys(dictVal, stripAssemblies);
                                else
                                    dropped = RemoveStrippedTypeKeysMafi(val, vt, ga[0], stripAssemblies);
                                if (dropped > 0)
                                {
                                    dictTypeEntriesDropped += dropped;
                                    if (logBudget-- > 0)
                                        progress?.Report($"    [dict-type-drop] {cur.Name}.{fi.Name} dropped {dropped} stripped-mod Type key(s)");
                                }
                            }
                        }
                    }

                    // (4) Continue BFS via class refs + struct walk for hidden Type fields.
                    var fvt = val.GetType();
                    if (fvt == typeof(string)) continue;
                    if (fvt.IsValueType && val is not System.Collections.IEnumerable)
                    {
                        for (var sCur = fvt; sCur is not null && sCur != typeof(object) && sCur != typeof(ValueType); sCur = sCur.BaseType)
                        {
                            FieldInfo[] sfields;
                            try { sfields = sCur.GetFields(allFlags); }
                            catch { continue; }
                            foreach (var sfi in sfields)
                            {
                                if (sfi.FieldType.IsPrimitive || sfi.FieldType.IsEnum || sfi.FieldType == typeof(string)) continue;
                                object? sv;
                                try { sv = sfi.GetValue(val); } catch { continue; }
                                if (sv is null) continue;
                                if (sv is Type st)
                                {
                                    if (ShouldStrip(st, stripAssemblies))
                                    {
                                        try
                                        {
                                            sfi.SetValue(val, null);
                                            fi.SetValue(obj, val);
                                            typeFieldsNulled++;
                                            if (logBudget-- > 0)
                                                progress?.Report($"    [type-null-struct] {cur.Name}.{fi.Name}.{sfi.Name} → {st.FullName}");
                                        }
                                        catch { }
                                    }
                                    continue;
                                }
                                var svt = sv.GetType();
                                if (!svt.IsValueType && svt != typeof(string) && visited.Add(sv) && queue.Count < QueueSizeCap) queue.Enqueue(sv);
                            }
                        }
                        continue;
                    }
                    if (visited.Add(val) && queue.Count < QueueSizeCap) queue.Enqueue(val);
                }
            }

            if (obj is System.Collections.IEnumerable enumerable && obj is not string)
            {
                System.Collections.IEnumerator? en = null;
                try { en = enumerable.GetEnumerator(); } catch { en = null; }
                if (en is not null)
                {
                    try
                    {
                        int idx = 0;
                        while (true)
                        {
                            bool moved;
                            try { moved = en.MoveNext(); } catch { break; }
                            if (!moved) break;
                            object? item;
                            try { item = en.Current; } catch { idx++; continue; }
                            if (item is null) { idx++; continue; }
                            var et = item.GetType();
                            if (!et.IsValueType && et != typeof(string) && visited.Add(item) && queue.Count < QueueSizeCap)
                                queue.Enqueue(item);
                            idx++;
                            if (idx > 1_000_000) break;
                        }
                    }
                    finally
                    {
                        if (en is IDisposable disp) try { disp.Dispose(); } catch { }
                    }
                }
            }
        }

        bool typeScrubBudgetHit = objectsScanned >= VisitBudget;
        progress?.Report(
            $"  Stripped-mod Type-reference scrub: nulled {typeFieldsNulled} Type field(s), " +
            $"dropped {lystTypeEntriesDropped} Lyst<Type> entry(ies), " +
            $"dropped {dictTypeEntriesDropped} Dict<Type,*> entry(ies) ({objectsScanned:N0} object(s) scanned{(typeScrubBudgetHit ? ", visit budget hit" : "")}).");
    }

    private static int CompactTypeListInPlace(object lyst, Type lystType, HashSet<string> stripAssemblies)
    {
        const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        FieldInfo? fiItems = null;
        FieldInfo? fiSize = null;
        for (var cur = lystType; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var fi in cur.GetFields(allInst))
            {
                if (fiItems is null && fi.FieldType.IsArray) fiItems = fi;
                if (fiSize is null && fi.FieldType == typeof(int)
                    && (fi.Name == "m_size" || fi.Name == "_size" || fi.Name.Contains("size") || fi.Name.Contains("Size")))
                    fiSize = fi;
            }
            if (fiItems is not null && fiSize is not null) break;
        }
        if (fiItems is null || fiSize is null) return 0;

        Array? arr;
        int size;
        try { arr = fiItems.GetValue(lyst) as Array; size = (int)(fiSize.GetValue(lyst) ?? 0); }
        catch { return 0; }
        if (arr is null || size <= 0) return 0;

        int write = 0, dropped = 0;
        for (int i = 0; i < size; i++)
        {
            object? slot;
            try { slot = arr.GetValue(i); } catch { slot = null; }
            if (slot is Type t && ShouldStrip(t, stripAssemblies)) { dropped++; continue; }
            if (write != i) { try { arr.SetValue(slot, write); } catch { } }
            write++;
        }
        for (int i = write; i < size; i++) { try { arr.SetValue(null, i); } catch { } }
        try { fiSize.SetValue(lyst, write); } catch { }
        return dropped;
    }

    private static int RemoveStrippedTypeKeys(System.Collections.IDictionary dict, HashSet<string> stripAssemblies)
    {
        var toRemove = new List<object>();
        foreach (System.Collections.DictionaryEntry e in dict)
        {
            if (e.Key is Type t && ShouldStrip(t, stripAssemblies))
                toRemove.Add(e.Key);
        }
        foreach (var k in toRemove)
        {
            try { dict.Remove(k); } catch { }
        }
        return toRemove.Count;
    }

    /// <summary>
    /// Removes stripped-mod Type keys from Mafi's custom Dict&lt;Type,V&gt; which does NOT
    /// implement System.Collections.IDictionary. Uses reflection to enumerate keys and
    /// call Remove(Type) for each stripped-mod entry. Handles both mutable Dict and
    /// read-only wrappers by silently swallowing Remove exceptions.
    /// </summary>
    private static int RemoveStrippedTypeKeysMafi(object dict, Type dictType, Type keyType, HashSet<string> stripAssemblies)
    {
        // Collect keys to remove by iterating via IEnumerable<KeyValuePair<TKey,TVal>>.
        // Mafi's Dict<T,V> implements IEnumerable<KeyValuePair<T,V>>.
        var toRemove = new List<Type>();
        try
        {
            if (dict is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is null) continue;
                    var itemType = item.GetType();
                    // KeyValuePair<Type, TValue> — get .Key
                    var kProp = itemType.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
                    if (kProp?.GetValue(item) is Type k && ShouldStrip(k, stripAssemblies))
                        toRemove.Add(k);
                }
            }
        }
        catch { return 0; }

        if (toRemove.Count == 0) return 0;

        // Call dict.Remove(Type) for each key to drop.
        var miRemove = dictType.GetMethod("Remove",
            BindingFlags.Public | BindingFlags.Instance,
            null, new[] { keyType }, null);
        if (miRemove is null) return 0;

        int removed = 0;
        foreach (var k in toRemove)
        {
            try { miRemove.Invoke(dict, new object[] { k }); removed++; }
            catch { }
        }
        return removed;
    }

    /// <summary>
    /// BFS the resolver graph hunting for any collection whose element type implements
    /// <c>IMessageNotification</c>, and remove every notification whose serialized state
    /// is broken in a way the game's notification UI can't render.
    /// <para/>
    /// Concrete failure mode (Promised Frontier deep3 game log):
    /// <code>
    /// DependencyResolverException: Failed to instantiate ResearchTab.
    ///   ---&gt; NullReferenceException
    ///        at ResearchTab+MessageUi.Value(ResearchFinishedMessage notification)
    ///        at ResearchTab.addNotification(IMessageNotification, isInit)
    /// </code>
    /// <c>MessageUi.Value</c> dereferences <c>notification.ResearchNode.Proto</c>. When
    /// <c>ResearchNode</c> is the <c>default</c> handle for a stripped-mod research
    /// proto (or an <c>Option&lt;ResearchNodeProto&gt;</c> with <c>HasValue == false</c>
    /// pointing at a healed-but-null target), the dereference NREs inside <c>ResearchTab</c>'s
    /// constructor — and because <c>ResearchTab</c> is a registered dependency, the whole
    /// game-load fails at <c>InstantiateAllAndLock</c>.
    /// <para/>
    /// We can't repair the saved notifications without reconstructing the original mod
    /// state, but the notifications carry zero gameplay impact (they're a UI history).
    /// So this pass conservatively drops any saved <c>IMessageNotification</c> that
    /// either:
    ///  - holds a phantom proto stub in any reference field, or
    ///  - holds a <c>null</c> in any field whose declared type is a Mafi proto (any class
    ///    deriving from <c>Mafi.Core.Prototypes.Proto</c>), or
    ///  - has a runtime type name containing <c>"Message"</c> AND any reference field
    ///    is null where the declared type is a class (cheap heuristic for serialised
    ///    notification structs whose proto handle was stripped).
    /// <para/>
    /// Operates on every <c>Lyst&lt;T&gt;</c> / <c>Dict&lt;K,V&gt;</c> / <c>IList</c>
    /// found whose element type is assignable to a type implementing
    /// <c>IMessageNotification</c>. Compacts in place using the same backing-array
    /// approach as <see cref="DropEventBaseCallbackEntriesForStrippedOwners"/>.
    /// </summary>
    private void DropBrokenSavedMessageNotifications(
        object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        // Resolve the IMessageNotification interface type from the loaded game assemblies.
        // Without it we can't identify notification collections; the pass is a no-op.
        var iNotif = AssemblyLoader.FindType("Mafi.Core.MessageNotifications.IMessageNotification");
        if (iNotif is null)
        {
            progress?.Report("  Broken-notification scrub: IMessageNotification type not loaded; skipping.");
            return;
        }

        var protoBase = AssemblyLoader.FindType("Mafi.Core.Prototypes.Proto");

        // Seed BFS with every reachable resolver object plus EntitiesManager entities.
        var roots = new List<object>();
        try
        {
            var fiResolved = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
            if (fiResolved?.GetValue(resolver) is System.Collections.IEnumerable resolved)
                foreach (var o in resolved) if (o is not null) roots.Add(o);
        }
        catch { }
        SeedRootsFromResolverDict(resolver, "m_resolvedInstancesByRealType", roots);
        SeedRootsFromResolverDict(resolver, "m_resolvedInstancesByRegisteredType", roots);
        try
        {
            var fiDisposed = FindFieldDeep(resolver.GetType(), "m_instancedToBeDisposed");
            if (fiDisposed?.GetValue(resolver) is System.Collections.IEnumerable disposed)
                foreach (var o in disposed) if (o is not null) roots.Add(o);
        }
        catch { }

        progress?.Report($"  Broken-notification scrub: BFS from {roots.Count} root(s) for IMessageNotification collections…");

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        foreach (var r in roots)
            if (visited.Add(r)) queue.Enqueue(r);

        // Track every notification instance seen during BFS so we can post-process even
        // if we never recognized its enclosing collection. Tracks (instance, runtimeTypeName).
        var notifInstancesSeen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var notifTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        int collectionsCompacted = 0;
        int notificationsDropped = 0;
        int objectsScanned = 0;
        int logBudget = 30;
        const int NotifVisitBudget  = 500_000;
        const int NotifQueueSizeCap = 300_000;

        while (queue.Count > 0 && objectsScanned < NotifVisitBudget)
        {
            var obj = queue.Dequeue();
            objectsScanned++;
            var objType = obj.GetType();

            // Census: if this object IS an IMessageNotification, remember it for the
            // post-BFS containment search. Cheap: a single interface-assignability check.
            if (iNotif.IsAssignableFrom(objType) && notifInstancesSeen.Add(obj))
            {
                var key = objType.FullName ?? objType.Name;
                notifTypeCounts[key] = notifTypeCounts.GetValueOrDefault(key) + 1;
            }

            // Walk fields to enqueue and to find notification-bearing collections.
            for (var cur = objType; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo[] fields;
                try { fields = cur.GetFields(allFlags); }
                catch { continue; }

                foreach (var fi in fields)
                {
                    var ft = fi.FieldType;
                    if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) continue;

                    object? val;
                    try { val = fi.GetValue(obj); } catch { continue; }
                    if (val is null) continue;

                    var vt = val.GetType();

                    // (1) Lyst<T> / IList where T implements IMessageNotification → compact.
                    //     Detection works on the GENERIC ARGUMENT regardless of whether the
                    //     runtime collection presents itself as System.Collections.IList,
                    //     so we use the generic-args probe even when the IList cast fails
                    //     (Mafi has custom collection types).
                    bool isCandidateList = false;
                    Type? listElemType = null;
                    if (vt.IsGenericType)
                    {
                        var ga = vt.GetGenericArguments();
                        if (ga.Length == 1 && iNotif.IsAssignableFrom(ga[0]))
                        {
                            isCandidateList = true;
                            listElemType = ga[0];
                        }
                    }
                    if (isCandidateList)
                    {
                        if (logBudget > 0)
                            progress?.Report($"    [notif-found-list] {cur.Name}.{fi.Name}  type={vt.Name}<{listElemType!.Name}>  count={TryGetCount(val)}");
                        int dropped = CompactNotificationListInPlace(val, vt, iNotif, protoBase, _phantomProtoStubs, allFlags);
                        if (dropped > 0)
                        {
                            collectionsCompacted++;
                            notificationsDropped += dropped;
                            if (logBudget-- > 0)
                                progress?.Report($"    [notif-drop] {cur.Name}.{fi.Name}: dropped {dropped} broken notification(s)");
                        }
                    }

                    // (2) Dict<K,V> where V or K implements IMessageNotification → remove keys.
                    bool isCandidateDict = false;
                    if (val is System.Collections.IDictionary && vt.IsGenericType)
                    {
                        var ga = vt.GetGenericArguments();
                        if (ga.Length == 2 && (iNotif.IsAssignableFrom(ga[0]) || iNotif.IsAssignableFrom(ga[1])))
                        {
                            isCandidateDict = true;
                        }
                    }
                    if (isCandidateDict)
                    {
                        if (logBudget > 0)
                            progress?.Report($"    [notif-found-dict] {cur.Name}.{fi.Name}  type={vt.Name}  count={TryGetCount(val)}");
                        int dropped = RemoveBrokenNotificationsFromDict((System.Collections.IDictionary)val, iNotif, protoBase, _phantomProtoStubs, allFlags);
                        if (dropped > 0)
                        {
                            collectionsCompacted++;
                            notificationsDropped += dropped;
                            if (logBudget-- > 0)
                                progress?.Report($"    [notif-dict-drop] {cur.Name}.{fi.Name}: dropped {dropped} broken notification(s)");
                        }
                    }

                    // (3) Field whose declared OR runtime type itself implements IMessageNotification:
                    //     null it out if broken. Catches scenarios where an Option<IMessageNotification>
                    //     or similar single-slot reference is held directly on a manager.
                    if (iNotif.IsAssignableFrom(vt))
                    {
                        if (IsNotificationBroken(val, iNotif, protoBase, _phantomProtoStubs, allFlags))
                        {
                            try
                            {
                                fi.SetValue(obj, null);
                                notificationsDropped++;
                                if (logBudget-- > 0)
                                    progress?.Report($"    [notif-null-field] {cur.Name}.{fi.Name} = null (broken {vt.Name})");
                            }
                            catch { }
                        }
                    }

                    // Continue BFS via class refs (skip strings, value types other than enumerables).
                    if (vt == typeof(string)) continue;
                    if (vt.IsValueType && val is not System.Collections.IEnumerable) continue;
                    if (visited.Add(val) && queue.Count < NotifQueueSizeCap) queue.Enqueue(val);
                }
            }

            // Walk enumerable contents to reach notification-bearing collections nested
            // inside other collections (e.g. Lyst inside a tuple value).
            if (obj is System.Collections.IEnumerable enumerable && obj is not string)
            {
                System.Collections.IEnumerator? en = null;
                try { en = enumerable.GetEnumerator(); } catch { en = null; }
                if (en is not null)
                {
                    try
                    {
                        int idx = 0;
                        while (true)
                        {
                            bool moved;
                            try { moved = en.MoveNext(); } catch { break; }
                            if (!moved) break;
                            object? item;
                            try { item = en.Current; } catch { idx++; continue; }
                            if (item is null) { idx++; continue; }
                            var et = item.GetType();
                            if (!et.IsValueType && et != typeof(string) && visited.Add(item) && queue.Count < NotifQueueSizeCap)
                                queue.Enqueue(item);
                            idx++;
                            if (idx > 1_000_000) break;
                        }
                    }
                    finally
                    {
                        if (en is IDisposable disp) try { disp.Dispose(); } catch { }
                    }
                }
            }
        }

        progress?.Report(
            $"  Broken-notification scrub: dropped {notificationsDropped} notification(s) " +
            $"from {collectionsCompacted} collection(s) ({objectsScanned:N0} object(s) scanned).");

        // Census report: how many IMessageNotification instances we actually visited.
        // If this is > 0 but collections-compacted was 0, the holders are unrecognized
        // collection shapes; the second pass below will look for them by walking every
        // visited object's fields/elements directly.
        if (notifInstancesSeen.Count > 0)
        {
            progress?.Report($"  Notification-instance census: {notifInstancesSeen.Count} instance(s) reachable; by type:");
            foreach (var (k, v) in notifTypeCounts.OrderByDescending(kv => kv.Value).Take(20))
                progress?.Report($"    × {v,5}  {k}");

            // Second pass: scorched-earth removal. For every container we visited, scan
            // its IList/IDictionary contents directly and physically remove any element
            // that's an IMessageNotification instance. Notifications are pure UI history
            // (no gameplay impact); dropping them all is safe and resolves the
            // ResearchTab/MessagesTab construction NREs caused by stripped-mod proto refs.
            int extraRemoved = ScrubReachableNotifInstancesFromContainers(visited, notifInstancesSeen, iNotif, progress);
            if (extraRemoved > 0)
            {
                notificationsDropped += extraRemoved;
                progress?.Report($"  Scorched-earth notif removal: dropped an additional {extraRemoved} instance(s).");
            }
        }
    }

    /// <summary>
    /// After the main BFS has identified every reachable <c>IMessageNotification</c>
    /// instance, this pass walks every container we visited and physically removes
    /// any element/key/value that's in <paramref name="notifInstances"/>.
    /// <para/>
    /// Container detection is layered for robustness:
    ///  1. Mafi <c>Lyst&lt;T&gt;</c>: detected by presence of an array field whose element
    ///     type matches a generic argument; we compact the backing <c>m_items</c> array
    ///     in place and rewrite <c>m_size</c>. This works regardless of <c>IList</c>
    ///     interface implementation.
    ///  2. <see cref="System.Collections.IDictionary"/>: physically remove offending keys.
    ///  3. <see cref="System.Collections.IList"/> fallback: for any non-array list-like
    ///     container that isn't a Mafi <c>Lyst&lt;T&gt;</c>.
    /// </summary>
    private static int ScrubReachableNotifInstancesFromContainers(
        HashSet<object> visited, HashSet<object> notifInstances, Type iNotif, IProgress<string>? progress)
    {
        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        int removed = 0;
        int logBudget = 30;

        foreach (var obj in visited)
        {
            var ot = obj.GetType();

            // (1) Mafi Lyst<T> shape: has an m_items array field and an m_size int field.
            //     Detect by structure, not by IList cast — Mafi's Lyst<T> implements IList<T>
            //     but in some configurations the non-generic IList interface dispatch may
            //     not behave as expected (e.g. Lyst<IInterface>.Remove(object) ambiguity).
            //     We compact the backing array in place using the same approach that
            //     CompactNotificationListInPlace uses for the targeted scrub.
            if (ot.IsGenericType && ot.Name.StartsWith("Lyst", StringComparison.Ordinal))
            {
                FieldInfo? fiItems = null;
                FieldInfo? fiSize = null;
                for (var cur = ot; cur is not null && cur != typeof(object); cur = cur.BaseType)
                {
                    foreach (var fi in cur.GetFields(allFlags))
                    {
                        if (fiItems is null && fi.FieldType.IsArray) fiItems = fi;
                        if (fiSize is null && fi.FieldType == typeof(int)
                            && (fi.Name == "m_size" || fi.Name == "_size"))
                            fiSize = fi;
                    }
                    if (fiItems is not null && fiSize is not null) break;
                }

                if (fiItems is not null && fiSize is not null)
                {
                    Array? arr;
                    int size;
                    try { arr = fiItems.GetValue(obj) as Array; size = (int)(fiSize.GetValue(obj) ?? 0); }
                    catch { arr = null; size = 0; }

                    if (arr is not null && size > 0)
                    {
                        int write = 0, dropped = 0;
                        for (int i = 0; i < size; i++)
                        {
                            object? slot;
                            try { slot = arr.GetValue(i); } catch { slot = null; }
                            if (slot is not null && notifInstances.Contains(slot))
                            {
                                dropped++;
                                continue;
                            }
                            if (write != i) { try { arr.SetValue(slot, write); } catch { } }
                            write++;
                        }
                        for (int i = write; i < size; i++) { try { arr.SetValue(null, i); } catch { } }

                        if (dropped > 0)
                        {
                            try { fiSize.SetValue(obj, write); } catch { }
                            removed += dropped;
                            if (logBudget-- > 0)
                                progress?.Report($"    [scorch-lyst] {ot.Name}: dropped {dropped} notification(s) (size {size}→{write})");
                        }
                        continue; // don't double-process via IList path below
                    }
                }
            }

            // (2) IDictionary: remove keys (or values) that are notifications.
            if (obj is System.Collections.IDictionary dict)
            {
                List<object>? keysToRemove = null;
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    bool keyIsNotif = e.Key is not null && notifInstances.Contains(e.Key);
                    bool valIsNotif = e.Value is not null && notifInstances.Contains(e.Value);
                    if (keyIsNotif || valIsNotif)
                    {
                        keysToRemove ??= new List<object>();
                        keysToRemove.Add(e.Key);
                    }
                }
                if (keysToRemove is not null)
                {
                    foreach (var k in keysToRemove)
                    {
                        try { dict.Remove(k); removed++; } catch { }
                    }
                    if (logBudget-- > 0)
                        progress?.Report($"    [scorch-dict] {ot.Name}: removed {keysToRemove.Count} notification(s)");
                }
                continue;
            }

            // (3) IList fallback for non-Lyst, non-array list types.
            if (obj is System.Collections.IList list && obj is not Array)
            {
                List<object>? toRemove = null;
                int count;
                try { count = list.Count; } catch { count = 0; }
                for (int i = 0; i < count; i++)
                {
                    object? item;
                    try { item = list[i]; } catch { continue; }
                    if (item is not null && notifInstances.Contains(item))
                    {
                        toRemove ??= new List<object>();
                        toRemove.Add(item);
                    }
                }
                if (toRemove is not null)
                {
                    foreach (var item in toRemove)
                    {
                        try { list.Remove(item); removed++; } catch { }
                    }
                    if (logBudget-- > 0)
                        progress?.Report($"    [scorch-list] {ot.Name}: removed {toRemove.Count} notification(s)");
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Best-effort element-count probe for a collection-like object. Tries
    /// <c>Count</c>/<c>Length</c> properties and the <c>m_size</c> field used by
    /// Mafi's <c>Lyst&lt;T&gt;</c>. Returns -1 if no count is available.
    /// </summary>
    private static int TryGetCount(object collection)
    {
        try
        {
            var t = collection.GetType();
            var p = t.GetProperty("Count") ?? t.GetProperty("Length");
            if (p is not null) { var v = p.GetValue(collection); if (v is int ci) return ci; }
            var f = t.GetField("m_size", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (f is not null) { var v = f.GetValue(collection); if (v is int fi) return fi; }
        }
        catch { }
        return -1;
    }

    /// <summary>
    /// Compacts a generic list of <c>IMessageNotification</c> in place: removes every
    /// element whose serialized state would NRE the game's notification UI on render.
    /// Uses the same <c>m_items</c> + <c>m_size</c> rewrite pattern Mafi's <c>Lyst&lt;T&gt;</c>
    /// uses internally.
    /// </summary>
    private static int CompactNotificationListInPlace(
        object lyst, Type lystType, Type iNotif, Type? protoBase,
        HashSet<object>? phantomStubs, BindingFlags allFlags)
    {
        FieldInfo? fiItems = null;
        FieldInfo? fiSize = null;
        for (var cur = lystType; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var fi in cur.GetFields(allFlags))
            {
                if (fiItems is null && fi.FieldType.IsArray) fiItems = fi;
                if (fiSize is null && fi.FieldType == typeof(int)
                    && (fi.Name == "m_size" || fi.Name == "_size" || fi.Name.Contains("size") || fi.Name.Contains("Size")))
                    fiSize = fi;
            }
            if (fiItems is not null && fiSize is not null) break;
        }
        if (fiItems is null || fiSize is null) return 0;

        Array? arr;
        int size;
        try { arr = fiItems.GetValue(lyst) as Array; size = (int)(fiSize.GetValue(lyst) ?? 0); }
        catch { return 0; }
        if (arr is null || size <= 0) return 0;

        int write = 0, dropped = 0;
        for (int i = 0; i < size; i++)
        {
            object? slot;
            try { slot = arr.GetValue(i); } catch { slot = null; }
            if (slot is not null && IsNotificationBroken(slot, iNotif, protoBase, phantomStubs, allFlags))
            {
                dropped++;
                continue;
            }
            if (write != i) { try { arr.SetValue(slot, write); } catch { } }
            write++;
        }
        for (int i = write; i < size; i++) { try { arr.SetValue(null, i); } catch { } }
        try { fiSize.SetValue(lyst, write); } catch { }
        return dropped;
    }

    /// <summary>
    /// Removes broken notifications from a dictionary where either the key or the value
    /// implements <c>IMessageNotification</c>.
    /// </summary>
    private static int RemoveBrokenNotificationsFromDict(
        System.Collections.IDictionary dict, Type iNotif, Type? protoBase,
        HashSet<object>? phantomStubs, BindingFlags allFlags)
    {
        var toRemove = new List<object>();
        foreach (System.Collections.DictionaryEntry e in dict)
        {
            object? key = e.Key;
            object? val = e.Value;
            object? notif = (key is not null && iNotif.IsAssignableFrom(key.GetType())) ? key
                           : (val is not null && iNotif.IsAssignableFrom(val.GetType())) ? val
                           : null;
            if (notif is null) continue;
            if (IsNotificationBroken(notif, iNotif, protoBase, phantomStubs, allFlags))
                toRemove.Add(key!);
        }
        foreach (var k in toRemove)
        {
            try { dict.Remove(k); } catch { }
        }
        return toRemove.Count;
    }

    /// <summary>
    /// Returns true if a saved notification's reference fields hold a phantom proto stub
    /// or a null where a Proto-typed reference is required. The notification UI
    /// dereferences these fields directly, so any null/phantom there is a guaranteed NRE
    /// during <c>ResearchTab</c> / <c>MessagesTab</c> construction.
    /// </summary>
    private static bool IsNotificationBroken(
        object notif, Type iNotif, Type? protoBase,
        HashSet<object>? phantomStubs, BindingFlags allFlags)
    {
        var nt = notif.GetType();
        for (var cur = nt; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            FieldInfo[] fields;
            try { fields = cur.GetFields(allFlags); }
            catch { continue; }

            foreach (var fi in fields)
            {
                var ft = fi.FieldType;
                if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) continue;

                object? val;
                try { val = fi.GetValue(notif); } catch { continue; }

                // Phantom-stub reference held in any field is a guaranteed render-time crash.
                if (val is not null && phantomStubs is not null && phantomStubs.Contains(val))
                    return true;

                // Null in a field whose declared type is a Proto subclass.
                if (val is null && protoBase is not null && !ft.IsValueType
                    && protoBase.IsAssignableFrom(ft))
                    return true;

                // Null in a field whose declared type ENDS in "Proto" (catches obfuscated
                // proto handles like ResearchNodeProto that may not derive from Proto in
                // a way reflection sees because of obfuscated base types).
                if (val is null && !ft.IsValueType
                    && (ft.Name.EndsWith("Proto", StringComparison.Ordinal)
                        || ft.Name.EndsWith("Node", StringComparison.Ordinal)))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Walks the entire resolver-reachable graph and, for every <c>Lyst&lt;CallbackSaveData&gt;</c>
    /// it finds (the backing storage of <see cref="EventBase{TAction}"/>'s saved subscribers),
    /// removes any slot whose <c>Owner</c> field references an instance whose runtime type
    /// belongs to a removed-mod assembly.
    /// <para/>
    /// Why this is necessary: <c>CallbackSaveData.Serialize</c> writes <c>Owner</c> via
    /// <c>writer.WriteGeneric</c>, which serialises the object's full assembly-qualified
    /// type name + fields inline into the resolver payload. So a SmartZipper or
    /// CargoShipDrydock captured as a callback target survives every reachability-based
    /// scrub: the holder is a struct in a list, the entity is reached only through a
    /// boxed-struct field walk, AND the entity may not even be in any other resolver
    /// collection (the <c>EventBase</c> may be the sole reference keeping it alive at
    /// re-serialise time).
    /// <para/>
    /// We compact the backing <c>m_items</c> array in place and update <c>m_size</c>,
    /// matching the same shape used by <see cref="RemoveFromLystBacking"/> and
    /// <see cref="ScrubStrippedRefsInStructLystSlots"/>.
    /// </summary>
    private void DropEventBaseCallbackEntriesForStrippedOwners(
        object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        if (stripAssemblies.Count == 0) return;

        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        // Collect roots: every resolver object + every entity in EntitiesManager + the
        // resolver's registered-instance dicts. Same coverage as the production
        // NullAllRemovedModEntityReferences pass so we mirror its 552k-object reach.
        var roots = new List<object>();
        try
        {
            var fiResolved = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
            if (fiResolved?.GetValue(resolver) is System.Collections.IEnumerable resolved)
                foreach (var o in resolved) if (o is not null) roots.Add(o);
        }
        catch { }
        SeedRootsFromResolverDict(resolver, "m_resolvedInstancesByRealType", roots);
        SeedRootsFromResolverDict(resolver, "m_resolvedInstancesByRegisteredType", roots);

        // Also seed from m_instancedToBeDisposed — the resolver writes this Lyst as part
        // of its serialization payload. Anything reachable only through this collection is
        // otherwise invisible to a BFS that seeds only from m_resolvedObjects + the dicts.
        try
        {
            var fiDisposed = FindFieldDeep(resolver.GetType(), "m_instancedToBeDisposed");
            if (fiDisposed?.GetValue(resolver) is System.Collections.IEnumerable disposed)
                foreach (var o in disposed) if (o is not null) roots.Add(o);
        }
        catch { }

        // NOTE: Do NOT seed from EntitiesManager here. With a populated resolver
        // (hydrated by DeserializeInto), m_resolvedObjects already contains EntityContext
        // which holds all 130K entities. Explicitly seeding them again doubles the queue
        // and, combined with BFS field-walking, explodes to 22GB+ RAM.

        progress?.Report($"  EventBase callback scrub: BFS from {roots.Count} root(s) for Lyst<CallbackSaveData>…");

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        foreach (var r in roots)
        {
            // Skip mod-assembly roots: they're being stripped, no need to clean them.
            if (!ShouldStrip(r.GetType(), stripAssemblies) && visited.Add(r))
                queue.Enqueue(r);
        }

        int listsScrubbed = 0;
        int entriesDropped = 0;
        int objectsScanned = 0;
        int logBudget = 200;
        const int VisitBudget = 500_000;
        const int QueueSizeCap = 300_000;

        while (queue.Count > 0 && objectsScanned < VisitBudget)
        {
            var obj = queue.Dequeue();
            objectsScanned++;
            var objType = obj.GetType();

            // Diagnostic: log first few PropertyModifier instances we visit so we can
            // see whether the BFS reaches the offending Action<bool> variant.
            if (objType.IsGenericType && objType.IsClass
                && (objType.FullName?.StartsWith("Mafi.Core.PropertiesDb.PropertyModifier", StringComparison.Ordinal) ?? false))
            {
                if (logBudget > 0)
                {
                    var ownerFi = FindFieldDeep(objType, "m_owner")
                                  ?? FindFieldDeep(objType, "Owner")
                                  ?? FindFieldDeep(objType, "<Owner>k__BackingField");
                    object? ownerVal = null;
                    try { ownerVal = ownerFi?.GetValue(obj); } catch { }
                    string ownerDesc = ownerVal is null ? "<null/unknown>" : ownerVal.GetType().FullName ?? ownerVal.GetType().Name;
                    bool ownerIsStripped = ownerVal is not null && ShouldStrip(ownerVal.GetType(), stripAssemblies);
                    progress?.Report($"    [pm-visit] {objType.FullName} owner={ownerDesc}{(ownerIsStripped ? " *** STRIPPED-OWNER ***" : "")}");
                    logBudget--;
                }
            }

            // Direct-scan EVERY visited object for any Lyst-of-CallbackSaveData field.
            // Bypasses any predicate ambiguity; cost is one extra field-walk per object.
            ScanObjectForCallbackSaveDataLysts(obj, stripAssemblies, ref entriesDropped, ref listsScrubbed, ref logBudget, progress);

            // Check whether THIS object is a Lyst<EventBase…+CallbackSaveData[…]> — the
            // backing storage of a saved subscriber list. If so, compact mod-Owner slots.
            //
            // Detection is intentionally lenient: we accept any single-generic-arg class
            // where the arg's FullName contains both "EventBase" and "CallbackSaveData".
            // This covers closed generic CallbackSaveData<Action<bool>> instantiations
            // whose Type.Name strips the type-arg suffix and whose DeclaringType may
            // surface as null on some reflection paths.
            if (objType.IsGenericType && objType.IsClass && !objType.IsArray)
            {
                var ga = objType.GetGenericArguments();
                if (ga.Length == 1 && ga[0].IsValueType)
                {
                    string elemFullName = ga[0].FullName ?? ga[0].Name;
                    bool isCallbackSaveDataLyst =
                        elemFullName.IndexOf("CallbackSaveData", StringComparison.Ordinal) >= 0
                        && elemFullName.IndexOf("EventBase", StringComparison.Ordinal) >= 0;

                    // Diagnostic: log every Lyst<struct> we encounter whose elem type
                    // looks remotely event-like, so we can spot near-misses if our
                    // lenient predicate ever fails to match a real holder.
                    if (logBudget > 0 && elemFullName.IndexOf("EventBase", StringComparison.Ordinal) >= 0
                        && !isCallbackSaveDataLyst)
                    {
                        progress?.Report($"    [near-miss] Lyst-shape with EventBase-like elem (NOT scrubbed): collection={objType.FullName}, elem={elemFullName}");
                        logBudget--;
                    }

                    if (isCallbackSaveDataLyst)
                    {
                        int dropped = CompactCallbackSaveDataLyst(obj, ga[0], stripAssemblies, progress, ref logBudget);
                        if (dropped > 0)
                        {
                            listsScrubbed++;
                            entriesDropped += dropped;
                        }
                        // [empty] log suppressed: previously drowned out [pm-visit] and Drop messages.
                    }
                }
            }

            // Walk this object's reference fields, enqueuing class values.
            for (var cur = objType; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo[] fields;
                try { fields = cur.GetFields(allFlags); }
                catch { continue; }

                foreach (var fi in fields)
                {
                    var ft = fi.FieldType;
                    if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) continue;

                    object? val;
                    try { val = fi.GetValue(obj); }
                    catch { continue; }
                    if (val is null) continue;
                    var vt = val.GetType();

                    // Struct field: box and walk its reference-type sub-fields so we
                    // reach class instances hidden behind value-type fields (e.g.
                    // PropertyModifier<T> reachable only via Property<T> struct).
                    // Mirrors NullAllRemovedModEntityReferences's struct-walk path
                    // which is why that pass reaches 552K objects vs our prior 341K.
                    if (vt.IsValueType && vt != typeof(string)
                        && val is not System.Collections.IEnumerable)
                    {
                        const BindingFlags structFieldFlags =
                            BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.Instance | BindingFlags.DeclaredOnly;
                        for (var sCur = vt; sCur is not null && sCur != typeof(object) && sCur != typeof(ValueType); sCur = sCur.BaseType)
                        {
                            FieldInfo[] sfields;
                            try { sfields = sCur.GetFields(structFieldFlags); }
                            catch { continue; }

                            foreach (var sfi in sfields)
                            {
                                if (sfi.FieldType.IsPrimitive || sfi.FieldType.IsEnum || sfi.FieldType == typeof(string)) continue;
                                object? sv;
                                try { sv = sfi.GetValue(val); } catch { continue; }
                                if (sv is null) continue;
                                var svt = sv.GetType();
                                if (svt.IsValueType || svt == typeof(string)) continue;
                                if (visited.Add(sv) && queue.Count < QueueSizeCap) queue.Enqueue(sv);
                            }
                        }
                        continue;
                    }

                    if (vt == typeof(string)) continue;
                    if (visited.Add(val) && queue.Count < QueueSizeCap) queue.Enqueue(val);
                }
            }

            // Walk this object's enumerable contents too (covers Lyst<T>, List<T>, Dict<K,V>,
            // arrays, Set<T>). A Lyst's elements are NOT exposed through field walking —
            // they live inside m_items[] which has no declared field on the array itself.
            if (obj is System.Collections.IEnumerable enumerable && obj is not string)
            {
                System.Collections.IEnumerator? en = null;
                try { en = enumerable.GetEnumerator(); } catch { en = null; }
                if (en is not null)
                {
                    try
                    {
                        int idx = 0;
                        while (true)
                        {
                            bool moved;
                            try { moved = en.MoveNext(); } catch { break; }
                            if (!moved) break;

                            object? item;
                            try { item = en.Current; } catch { idx++; continue; }
                            if (item is null) { idx++; continue; }

                            var et = item.GetType();
                            if (!et.IsValueType && et != typeof(string) && visited.Add(item) && queue.Count < QueueSizeCap)
                                queue.Enqueue(item);

                            idx++;
                            if (idx > 1_000_000) break;
                        }
                    }
                    finally
                    {
                        if (en is IDisposable disp) try { disp.Dispose(); } catch { }
                    }
                }
            }
        }

        bool budgetHit = objectsScanned >= VisitBudget;
        progress?.Report($"  EventBase callback scrub: dropped {entriesDropped} stripped-Owner entry(ies) across {listsScrubbed} Lyst<CallbackSaveData>(s) ({objectsScanned:N0} object(s) scanned{(budgetHit ? ", visit budget hit" : "")}).");
    }

    /// <summary>
    /// Walks every field on <paramref name="obj"/> (declared + inherited) and processes
    /// any field whose value is a generic class with one struct-type-arg whose FullName
    /// contains "CallbackSaveData". This is the bypass-all-predicates path that fires on
    /// any PropertyModifier instance — it also catches the inherited <c>EventBase</c>
    /// fields (e.g. <c>m_callbacksSaveData</c>) regardless of how reflection's nested-
    /// type metadata exposes them.
    /// </summary>
    private static void ScanObjectForCallbackSaveDataLysts(
        object obj, HashSet<string> stripAssemblies,
        ref int entriesDropped, ref int listsScrubbed,
        ref int logBudget, IProgress<string>? progress)
    {
        const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var objType = obj.GetType();

        for (var cur = objType; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            FieldInfo[] fields;
            try { fields = cur.GetFields(allInst); }
            catch { continue; }

            foreach (var fi in fields)
            {
                var ft = fi.FieldType;
                if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) continue;

                object? val;
                try { val = fi.GetValue(obj); } catch { continue; }
                if (val is null) continue;

                var vt = val.GetType();
                if (!vt.IsGenericType || !vt.IsClass || vt.IsArray) continue;

                var ga = vt.GetGenericArguments();
                if (ga.Length != 1 || !ga[0].IsValueType) continue;

                string elemFn = ga[0].FullName ?? ga[0].Name;
                if (elemFn.IndexOf("CallbackSaveData", StringComparison.Ordinal) < 0) continue;

                if (logBudget > 0)
                {
                    progress?.Report($"    [direct-scan] Found CallbackSaveData Lyst on {cur.Name}.{fi.Name}: elem={elemFn}");
                    logBudget--;
                }

                int dropped = CompactCallbackSaveDataLyst(val, ga[0], stripAssemblies, progress, ref logBudget);
                if (dropped > 0)
                {
                    listsScrubbed++;
                    entriesDropped += dropped;
                }
            }
        }
    }

    /// <summary>
    /// Compacts a <c>Lyst&lt;CallbackSaveData&gt;</c> in place: removes every slot whose
    /// boxed struct's <c>Owner</c> field references an object whose runtime type is in
    /// <paramref name="stripAssemblies"/>. Returns the number of entries removed.
    /// </summary>
    private static int CompactCallbackSaveDataLyst(
        object lyst, Type elemType, HashSet<string> stripAssemblies,
        IProgress<string>? progress, ref int logBudget)
    {
        const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Find m_items + m_size (or matching) on the Lyst.
        FieldInfo? fiItems = null;
        FieldInfo? fiSize = null;
        for (var cur = lyst.GetType(); cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var fi in cur.GetFields(allInst | BindingFlags.DeclaredOnly))
            {
                if (fiItems is null && fi.FieldType.IsArray) fiItems = fi;
                if (fiSize is null && fi.FieldType == typeof(int)
                    && (fi.Name == "m_size" || fi.Name == "_size" || fi.Name.Contains("size") || fi.Name.Contains("Size")))
                    fiSize = fi;
            }
            if (fiItems is not null && fiSize is not null) break;
        }
        if (fiItems is null || fiSize is null) return 0;

        Array? arr;
        int size;
        try
        {
            arr = fiItems.GetValue(lyst) as Array;
            size = (int)(fiSize.GetValue(lyst) ?? 0);
        }
        catch { return 0; }
        if (arr is null || size <= 0) return 0;

        // CallbackSaveData has fields: Owner (object), DeclaringType (Type), MethodName (string).
        // Both Owner and DeclaringType are serialised inline via WriteGeneric — so EITHER
        // referencing a stripped-mod assembly causes the validator to fail. We must check both.
        var fiOwner = elemType.GetField("Owner", allInst);
        var fiDeclaringType = elemType.GetField("DeclaringType", allInst);
        if (fiOwner is null && fiDeclaringType is null) return 0;

        int write = 0;
        int dropped = 0;
        for (int i = 0; i < size; i++)
        {
            object? slot;
            try { slot = arr.GetValue(i); }
            catch { slot = null; }

            bool drop = false;
            string? dropReason = null;
            if (slot is not null)
            {
                // Check Owner (instance reference). Its runtime type is what gets serialised.
                if (fiOwner is not null)
                {
                    object? owner;
                    try { owner = fiOwner.GetValue(slot); } catch { owner = null; }
                    if (owner is not null && ShouldStrip(owner.GetType(), stripAssemblies))
                    {
                        drop = true;
                        dropReason = $"owner={owner.GetType().FullName}";
                    }
                }

                // Check DeclaringType (Type field — IS the AQN string when serialised).
                // The CallbackSaveData was registered against a method on a stripped-mod
                // class; even if Owner is null/vanilla, this field alone causes the AQN
                // to land in the binary. Same fix: drop the entire entry.
                if (!drop && fiDeclaringType is not null)
                {
                    object? declType;
                    try { declType = fiDeclaringType.GetValue(slot); } catch { declType = null; }
                    if (declType is Type t && ShouldStrip(t, stripAssemblies))
                    {
                        drop = true;
                        dropReason = $"declaringType={t.FullName}";
                    }
                }

                if (drop && logBudget-- > 0)
                    progress?.Report($"    Dropped CallbackSaveData[{i}] {dropReason}");
            }

            if (drop) { dropped++; continue; }

            if (write != i) { try { arr.SetValue(slot, write); } catch { } }
            write++;
        }
        // Clear vacated tail slots. For value-type arrays, SetValue(null, i) throws
        // InvalidCastException — every throw allocates a stack trace and can lead
        // to OutOfMemoryException when many entries are dropped from a large Lyst.
        // Use the element type's default boxed value instead (one allocation total).
        var elementType = arr.GetType().GetElementType();
        object? defaultSlot = null;
        if (elementType is not null && elementType.IsValueType)
        {
            try { defaultSlot = Activator.CreateInstance(elementType); }
            catch { defaultSlot = null; }
        }
        for (int i = write; i < size; i++)
        {
            try { arr.SetValue(defaultSlot, i); } catch { }
        }
        try { fiSize.SetValue(lyst, write); } catch { }
        return dropped;
    }

    /// <summary>
    /// Walks the underlying backing array of a Mafi <c>Lyst&lt;T&gt;</c>-style collection
    /// (or any array reachable via a public <c>m_items</c> field) where <c>T</c> is a
    /// <b>struct</b>. For each slot, reads the boxed struct, walks its reference-type
    /// fields, nulls any whose value's runtime type is in <paramref name="stripAssemblies"/>,
    /// and writes the modified box back to the array slot.
    /// <para/>
    /// Solves the case the BFS in <see cref="NullAllRemovedModEntityReferences"/> can't:
    /// when the holder of a stripped-entity reference is a struct stored as an element
    /// of a <c>Lyst&lt;StructWithEntityField&gt;</c>, the BFS' <c>foreach</c> boxes
    /// each element and the value-type skip-rule discards it. The BlobWriter, however,
    /// serialises every element of every collection field — so any stripped ref hidden
    /// behind a struct slot would still be written into the binary. This method is the
    /// only path that actually reaches into those slots and severs the ref before
    /// re-serialisation.
    /// </summary>
    private static void ScrubStrippedRefsInStructLystSlots(
        object collection, HashSet<string> stripAssemblies,
        ref int nulled, ref int logBudget, IProgress<string>? progress)
    {
        if (collection is null) return;
        var collType = collection.GetType();

        // Case 1: collection IS itself an Array (e.g. T[]). Walk it directly.
        Array? arr = collection as Array;
        int size = arr?.Length ?? 0;

        // Case 2: Lyst<T>/List<T>-shape — find m_items array + m_size logical size.
        if (arr is null)
        {
            const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo? fiItems = null;
            FieldInfo? fiSize = null;
            for (var cur = collType; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                foreach (var fi in cur.GetFields(allInst | BindingFlags.DeclaredOnly))
                {
                    if (fiItems is null && fi.FieldType.IsArray) fiItems = fi;
                    if (fiSize is null && fi.FieldType == typeof(int)
                        && (fi.Name == "m_size" || fi.Name == "_size" || fi.Name.Contains("size") || fi.Name.Contains("Size")))
                        fiSize = fi;
                }
                if (fiItems is not null && fiSize is not null) break;
            }
            if (fiItems is null) return;

            try { arr = fiItems.GetValue(collection) as Array; }
            catch { return; }
            if (arr is null) return;

            try { size = (int)(fiSize?.GetValue(collection) ?? arr.Length); }
            catch { size = arr.Length; }
        }

        if (arr is null || size <= 0) return;

        var elemType = arr.GetType().GetElementType();
        if (elemType is null || !elemType.IsValueType || elemType.IsPrimitive || elemType.IsEnum) return;
        if (elemType == typeof(string)) return;

        const BindingFlags allInst2 = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        for (int i = 0; i < size; i++)
        {
            object? boxed;
            try { boxed = arr.GetValue(i); }
            catch { continue; }
            if (boxed is null) continue;

            bool boxModified = false;

            // Walk every reference-type field on the struct. A struct may have several
            // class-typed fields (e.g. StatsEntry { Entity Owner; Producer Producer; }).
            for (var cur = elemType; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo[] sfields;
                try { sfields = cur.GetFields(allInst2 | BindingFlags.DeclaredOnly); }
                catch { continue; }

                foreach (var sfi in sfields)
                {
                    var sft = sfi.FieldType;
                    if (sft.IsValueType || sft == typeof(string)) continue;

                    object? sv;
                    try { sv = sfi.GetValue(boxed); }
                    catch { continue; }
                    if (sv is null) continue;

                    var svType = sv.GetType();
                    if (svType.IsValueType) continue;
                    if (!ShouldStrip(svType, stripAssemblies)) continue;

                    try
                    {
                        sfi.SetValue(boxed, null);
                        boxModified = true;
                        nulled++;
                        if (logBudget-- > 0)
                            progress?.Report($"    Nulled struct-slot {collType.Name}[{i}].{elemType.Name}.{sfi.Name} → {svType.FullName}");
                    }
                    catch { }
                }
            }

            if (boxModified)
            {
                try { arr.SetValue(boxed, i); }
                catch { /* slot type-mismatch — extremely unlikely; box is correct elemType */ }
            }
        }
    }

    // ── Targeted IoPort connection scrub ─────────────────────────────────
    //
    // After entity stripping, vanilla IoPorts may still hold a ConnectedPort
    // (Option<IoPort>) pointing to a now-stripped mod entity's IoPort. BlobWriter
    // follows: VanillaIoPort → ConnectedPort → StrippedIoPort → OwnerEntity →
    // ModEntity AQN → CorruptedSaveException.
    //
    // The BFS in NullAllRemovedModEntityReferences can't reliably reach these
    // connections within the visit budget when there are 84k+ entity roots and
    // hundreds of thousands of IoPort sub-objects.
    //
    // This pass gets ALL IoPort objects directly from IoPortsManager.m_ports
    // (the global registry), finds those whose OwnerEntity is stripped, nulls
    // their OwnerEntity, and also nulls the ConnectedPort on any vanilla IoPort
    // that was connected to a now-stripped port. O(total_ports), no BFS budget.

    /// <summary>
    /// Directly iterates <c>IoPortsManager.m_ports</c> (the global IoPort registry)
    /// to sever connections from vanilla IoPorts to stripped-mod IoPorts, and to
    /// null <c>OwnerEntity</c> on stripped IoPorts. This is the targeted fix for
    /// the AQN path: <c>VanillaIoPort → ConnectedPort → StrippedIoPort → OwnerEntity → ModAQN</c>.
    /// </summary>
    private void ScrubDanglingIoPortConnections(
        object resolver, HashSet<string> stripAssemblies, IProgress<string>? progress)
    {
        if (stripAssemblies.Count == 0) return;

        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ── Step 1: find IoPortsManager in m_resolvedObjects ─────────────
        var fiResolved = FindFieldDeep(resolver.GetType(), "m_resolvedObjects");
        if (fiResolved?.GetValue(resolver) is not System.Collections.IEnumerable resolved)
        {
            progress?.Report("  [IoPort scrub] m_resolvedObjects not found — skipping.");
            return;
        }

        var tIoPortsManager = AssemblyLoader.FindType("Mafi.Core.Ports.Io.IoPortsManager");
        if (tIoPortsManager is null)
        {
            progress?.Report("  [IoPort scrub] IoPortsManager type not found — skipping.");
            return;
        }

        object? ioPortsManager = null;
        foreach (var obj in resolved)
        {
            if (obj is not null && tIoPortsManager.IsAssignableFrom(obj.GetType()))
            { ioPortsManager = obj; break; }
        }
        if (ioPortsManager is null)
        {
            progress?.Report("  [IoPort scrub] IoPortsManager instance not found in resolver — skipping.");
            return;
        }

        // ── Step 2: get m_ports ───────────────────────────────────────────
        var fiPorts = FindFieldDeep(ioPortsManager.GetType(), "m_ports");
        if (fiPorts is null)
        {
            progress?.Report("  [IoPort scrub] m_ports field not found on IoPortsManager — skipping.");
            return;
        }
        var portsRaw = fiPorts.GetValue(ioPortsManager);
        if (portsRaw is null)
        {
            progress?.Report("  [IoPort scrub] m_ports is null — skipping.");
            return;
        }

        // ── Step 3: extract IoPort values from the dictionary ─────────────
        // m_ports is a Dict<IoPortId, IoPort>.  When Phase 3 is skipped,
        // Dict.initAfterLoad hasn't run yet: m_buckets is null so IsInitializedAfterLoad
        // is false and GetEnumerator() throws "not initialized after load."
        // Dict.initAfterLoad is safe to call in isolation — it just rebuilds the hash
        // table from the already-populated m_entries array (no external state needed).
        // We call it here so the rest of the scrub can use normal enumeration and Remove().
        var piIsInit = portsRaw.GetType().GetProperty("IsInitializedAfterLoad",
            BindingFlags.Public | BindingFlags.Instance);
        if (piIsInit?.GetValue(portsRaw) is false)
        {
            progress?.Report("  [IoPort scrub] m_ports not yet initialized — calling initAfterLoad to rebuild hash table.");
            var miDictInit = portsRaw.GetType().GetMethod("initAfterLoad",
                BindingFlags.NonPublic | BindingFlags.Instance);
            try { miDictInit?.Invoke(portsRaw, null); }
            catch (Exception ex)
            {
                progress?.Report($"  [IoPort scrub] initAfterLoad failed ({ex.GetBaseException().Message}) — skipping IoPort scrub.");
                return;
            }
        }

        var portsCollection = portsRaw as System.Collections.IEnumerable;
        if (portsCollection is null)
        {
            progress?.Report("  [IoPort scrub] m_ports is not IEnumerable — skipping.");
            return;
        }

        var allPorts = new List<object>();
        foreach (var kv in portsCollection)
        {
            if (kv is null) continue;
            var kvType = kv.GetType();
            // Try Value property first (standard KVP / Mafi dict enumerator).
            var valProp = kvType.GetProperty("Value");
            object? port = valProp?.GetValue(kv);
            if (port is null)
            {
                // Fallback: take the first non-value-type field in the KVP struct.
                foreach (var f in kvType.GetFields(allFlags))
                {
                    if (!f.FieldType.IsValueType)
                    {
                        var candidate = f.GetValue(kv);
                        if (candidate is not null) { port = candidate; break; }
                    }
                }
            }
            if (port is not null) allPorts.Add(port);
        }

        progress?.Report($"  [IoPort scrub] {allPorts.Count} IoPort(s) found in IoPortsManager.m_ports.");
        if (allPorts.Count == 0) return;

        // ── Step 4: reflect on the IoPort class ───────────────────────────
        var tIoPort   = allPorts[0].GetType().BaseType ?? allPorts[0].GetType();
        // Walk the whole hierarchy once from a concrete port instance to find fields.
        var portExample = allPorts[0];
        var tPortConcrete = portExample.GetType();

        FieldInfo? fiOwner = null;
        FieldInfo? fiConnectedBacking = null;

        for (var cur = tPortConcrete; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var fi in cur.GetFields(allFlags | BindingFlags.DeclaredOnly))
            {
                if (fiOwner is null &&
                    (fi.Name == "OwnerEntity" || fi.Name == "m_ownerEntity" ||
                     fi.Name.EndsWith("OwnerEntity", StringComparison.OrdinalIgnoreCase)))
                    fiOwner = fi;

                if (fiConnectedBacking is null &&
                    (fi.Name.Contains("ConnectedPort") || fi.Name.Contains("connectedPort")))
                    fiConnectedBacking = fi;
            }
            if (fiOwner is not null && fiConnectedBacking is not null) break;
        }

        if (fiOwner is null)
            progress?.Report("  [IoPort scrub] OwnerEntity field not found on IoPort — owner-null step skipped.");
        if (fiConnectedBacking is null)
            progress?.Report("  [IoPort scrub] ConnectedPort backing field not found on IoPort — conn-null step skipped.");

        // ── Step 5: identify stripped IoPorts (owner is in a stripped assembly) ──
        // Three sources qualify a port as orphaned:
        //   (a) Owner's runtime type is in a removed-mod assembly (original behaviour).
        //   (b) Owner is one of the vanilla-typed entities we stripped because their
        //       internal state was unrecoverable (broken trajectory / phantom port proto).
        //   (c) Owner is null OR owner is not present in EntitiesManager.m_entitiesLinear.
        //       Catches ports orphaned by earlier scrubs (or by previous deep-edit runs)
        //       whose owner ref no longer points to a live entity. Without this, the
        //       renderer's IoPortsRenderer.initState iterates orphan ports and NREs in
        //       InstancedChunkBasedLayoutEntitiesRenderer.GetBlueprintColor(null).
        // Build a fast membership set of currently-live entities for the owner check.
        var liveEntitySet = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var tEntitiesManager = AssemblyLoader.FindType("Mafi.Core.Entities.EntitiesManager");
        if (tEntitiesManager is not null)
        {
            foreach (var obj in resolved)
            {
                if (obj is null || !tEntitiesManager.IsAssignableFrom(obj.GetType())) continue;
                var fiLinear = FindFieldDeep(obj.GetType(), "m_entitiesLinear");
                if (fiLinear?.GetValue(obj) is System.Collections.IEnumerable liveLinear)
                {
                    foreach (var e in liveLinear) if (e is not null) liveEntitySet.Add(e);
                }
                break;
            }
        }

        var strippedPorts = new HashSet<object>(ReferenceEqualityComparer.Instance);
        int reasonModAsm = 0, reasonStrippedSet = 0, reasonOrphan = 0;
        if (fiOwner is not null)
        {
            foreach (var port in allPorts)
            {
                try
                {
                    var owner = fiOwner.GetValue(port);

                    // (c1) Owner already null → orphan.
                    if (owner is null)
                    {
                        if (strippedPorts.Add(port)) reasonOrphan++;
                        continue;
                    }

                    // (a) Owner type is in a removed-mod assembly.
                    if (ShouldStrip(owner.GetType(), stripAssemblies))
                    {
                        if (strippedPorts.Add(port)) reasonModAsm++;
                        continue;
                    }

                    // (b) Owner is in our broken-entity strip set.
                    if (_strippedBrokenEntities is not null && _strippedBrokenEntities.Contains(owner))
                    {
                        if (strippedPorts.Add(port)) reasonStrippedSet++;
                        continue;
                    }

                    // (c2) Owner is a live-looking object but not in m_entitiesLinear:
                    //      the owner was removed by some earlier pass without the port
                    //      manager being notified.
                    if (liveEntitySet.Count > 0 && !liveEntitySet.Contains(owner))
                    {
                        if (strippedPorts.Add(port)) reasonOrphan++;
                    }
                }
                catch { }
            }
        }

        progress?.Report($"  [IoPort scrub] {strippedPorts.Count} stripped IoPort(s): {reasonModAsm} mod-asm + {reasonStrippedSet} broken-entity + {reasonOrphan} orphan(null/not-in-linear).");

        // ── Step 6: null ConnectedPort on vanilla ports that connect to stripped ports ──
        int nulledConn = 0;
        if (fiConnectedBacking is not null && strippedPorts.Count > 0)
        {
            var ftOption = fiConnectedBacking.FieldType; // Option<IoPort> struct

            // Find the inner IoPort reference field inside Option<IoPort>.
            // For a reference-typed T, Option<T> typically has one reference field (the value).
            FieldInfo? fiOptionInner = null;
            foreach (var sfi in ftOption.GetFields(allFlags))
            {
                if (!sfi.FieldType.IsValueType && sfi.FieldType != typeof(string))
                { fiOptionInner = sfi; break; }
            }

            // Default (empty) Option<IoPort> value to assign when clearing.
            object? defaultOption = null;
            try { defaultOption = Activator.CreateInstance(ftOption); } catch { }

            foreach (var port in allPorts)
            {
                if (strippedPorts.Contains(port)) continue;
                try
                {
                    var connBoxed = fiConnectedBacking.GetValue(port);
                    if (connBoxed is null) continue;

                    // Get the inner IoPort reference from the Option struct.
                    object? connectedPort = fiOptionInner?.GetValue(connBoxed);
                    if (connectedPort is null) continue;

                    if (strippedPorts.Contains(connectedPort))
                    {
                        // Disconnect this vanilla port from the stripped port.
                        fiConnectedBacking.SetValue(port, defaultOption);
                        nulledConn++;
                    }
                }
                catch { }
            }
        }

        // ── Step 7: null OwnerEntity on stripped IoPorts ──────────────────
        int nulledOwner = 0;
        if (fiOwner is not null)
        {
            foreach (var port in strippedPorts)
            {
                try { fiOwner.SetValue(port, null); nulledOwner++; } catch { }
            }
        }

        progress?.Report($"  [IoPort scrub] Nulled OwnerEntity on {nulledOwner} stripped port(s); " +
                         $"disconnected {nulledConn} vanilla port(s) from stripped ports.");

        // ── Step 8: physically remove stripped IoPorts from IoPortsManager.m_ports ──
        // Why: nulling OwnerEntity above is enough for the serializer (the port no
        // longer references a stripped entity), but the game still sees the port at
        // load time and IoPortsRenderer.initState calls GetBlueprintColor(port.OwnerEntity)
        // → NRE because OwnerEntity is now null. Removing the port from the manager's
        // dict prevents the renderer from ever seeing it.
        if (strippedPorts.Count > 0)
        {
            int removedFromDict = RemovePortsFromManagerDict((System.Collections.IEnumerable)portsRaw, fiPorts, ioPortsManager, strippedPorts, allFlags);
            progress?.Report($"  [IoPort scrub] Removed {removedFromDict} stripped port(s) from IoPortsManager.m_ports.");
        }
    }

    /// <summary>
    /// Removes every IoPort in <paramref name="strippedPorts"/> from the
    /// <c>IoPortsManager.m_ports</c> dictionary. The dict is keyed by IoPortId; we
    /// enumerate it once to collect keys whose value is in the stripped set, then
    /// invoke <c>Remove(key)</c> reflectively.
    /// <para/>
    /// Used after the OwnerEntity-null pass so the renderer's
    /// <c>IoPortsRenderer.initState</c> never iterates these orphaned ports
    /// (which would NRE in <c>GetBlueprintColor(null entity)</c>).
    /// </summary>
    private static int RemovePortsFromManagerDict(
        System.Collections.IEnumerable portsCollection,
        FieldInfo fiPorts,
        object ioPortsManager,
        HashSet<object> strippedPorts,
        BindingFlags allFlags)
    {
        // Enumerate the dict once to find KVPs whose Value is a stripped port; collect
        // the corresponding Keys.
        var keysToRemove = new List<object>();
        foreach (var kv in portsCollection)
        {
            if (kv is null) continue;
            var kvType = kv.GetType();
            object? key = kvType.GetProperty("Key")?.GetValue(kv);
            object? port = kvType.GetProperty("Value")?.GetValue(kv);
            if (port is null || key is null) continue;
            if (strippedPorts.Contains(port)) keysToRemove.Add(key);
        }

        if (keysToRemove.Count == 0) return 0;

        // Find a Remove(TKey) method on the dict. Mafi's Dict<TKey,TValue> mirrors
        // System.Collections.Generic.Dictionary's API surface.
        var dict = fiPorts.GetValue(ioPortsManager);
        if (dict is null) return 0;
        var removeMethod = dict.GetType().GetMethods(allFlags)
            .FirstOrDefault(m => m.Name == "Remove" && m.GetParameters().Length == 1);
        if (removeMethod is null) return 0;

        int removed = 0;
        foreach (var key in keysToRemove)
        {
            try
            {
                var paramType = removeMethod.GetParameters()[0].ParameterType;
                if (!paramType.IsAssignableFrom(key.GetType())) continue;
                var result = removeMethod.Invoke(dict, new[] { key });
                if (result is not bool b || b) removed++;
            }
            catch { }
        }
        return removed;
    }
}
