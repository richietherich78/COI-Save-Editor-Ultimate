using System.Reflection;

namespace COISaveEditorUltimate.DeepEdit;

public sealed partial class DeepEditEngine
{
    private record struct AuditFinding(
        string ProtoTypeName,
        string OwnerTypeName,
        string FieldPath,
        string ContainerType,
        int Count);

    /// <summary>
    /// Scans every known root object and its fields (flat — no recursive BFS expansion)
    /// to find phantom proto references. Fast, non-hanging alternative to full graph walk.
    /// Does NOT modify anything.
    /// </summary>
    internal void AuditPhantomProtoRefs(object resolver, IProgress<string>? progress)
    {
        if (_phantomProtoStubs is null || _phantomProtoStubs.Count == 0)
        {
            progress?.Report("  AUDIT: No phantom stubs — nothing to audit.");
            return;
        }

        progress?.Report($"  AUDIT: Scanning for {_phantomProtoStubs.Count} phantom stub(s)…");

        var tProto = AssemblyLoader.FindType("Mafi.Core.Prototypes.Proto");
        var declaredOnly = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        // ── Collect every root object we know about ───────────────────────
        var roots = new List<(object obj, string label)>();

        void AddRoots(System.Collections.IEnumerable? src, string labelPrefix)
        {
            if (src is null) return;
            foreach (var o in src.Cast<object>())
                if (o is not null) roots.Add((o, labelPrefix + o.GetType().Name));
        }

        AddRoots(
            (FindFieldDeep(resolver.GetType(), "m_resolvedObjects")?.GetValue(resolver)
                as System.Collections.IEnumerable),
            "");

        if (FindFieldDeep(resolver.GetType(), "m_resolvedInstancesByRealType")
                ?.GetValue(resolver) is System.Collections.IEnumerable byType)
        {
            foreach (var kv in byType.Cast<object>())
            {
                var v = kv?.GetType().GetProperty("Value")?.GetValue(kv);
                if (v is not null && roots.All(r => r.obj != v))
                    roots.Add((v, "byType:" + v.GetType().Name));
            }
        }

        // Entities are intentionally NOT added to roots here.
        // On large saves m_entitiesLinear can contain hundreds of thousands of entries;
        // scanning each one flat would make the audit take minutes or hang entirely.
        // The audit is purely informational — phantom refs inside individual entities
        // are handled (and removed) by StripPhantomProtoRefsFromCollections.

        progress?.Report($"  AUDIT: {roots.Count} resolver objects to scan (entities excluded to avoid hang on large saves).");

        var findings = new List<AuditFinding>();

        foreach (var (obj, label) in roots)
        {
            try { ScanObjectFlat(obj, label, declaredOnly, tProto, findings); }
            catch { }
        }

        progress?.Report($"  AUDIT: Scan complete.");

        if (findings.Count == 0)
        {
            progress?.Report("  AUDIT: ✓ No phantom proto references found.");
            return;
        }

        int totalRefs = findings.Sum(f => f.Count);
        progress?.Report($"  AUDIT: *** {totalRefs} phantom ref(s) across {findings.Count} site(s) ***");

        foreach (var grp in findings.GroupBy(f => f.ProtoTypeName).OrderBy(g => g.Key))
        {
            int grpTotal = grp.Sum(f => f.Count);
            progress?.Report($"  AUDIT:   [{grp.Key}] — {grpTotal} ref(s) at {grp.Count()} site(s):");
            foreach (var f in grp.OrderBy(f => f.FieldPath))
                progress?.Report($"  AUDIT:     {f.OwnerTypeName}.{f.FieldPath}  ({f.ContainerType}) × {f.Count}");
        }
    }

    private void ScanObjectFlat(
        object obj, string label,
        BindingFlags declaredOnly, Type? tProto,
        List<AuditFinding> findings)
    {
        var type = obj.GetType();
        for (var cur = type; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var fi in cur.GetFields(declaredOnly))
            {
                try
                {
                    CheckField(fi, obj, type.Name, fi.Name, declaredOnly, tProto, findings, depth: 0);
                }
                catch { }
            }
        }
    }

    private void CheckField(
        FieldInfo fi, object owner, string ownerTypeName, string fieldPath,
        BindingFlags declaredOnly, Type? tProto,
        List<AuditFinding> findings, int depth)
    {
        if (depth > 4) return;
        if (fi.FieldType.IsPrimitive || fi.FieldType.IsEnum) return;
        if (fi.FieldType == typeof(string)) return;

        object? val;
        try { val = fi.GetValue(owner); } catch { return; }
        if (val is null) return;

        // ── Direct phantom reference ─────────────────────────────────────
        if (!fi.FieldType.IsValueType && _phantomProtoStubs!.Contains(val))
        {
            findings.Add(new AuditFinding(val.GetType().Name, ownerTypeName, fieldPath, "direct ref", 1));
            return;
        }

        // ── Value-type struct field: recurse one level into its own fields ─
        if (fi.FieldType.IsValueType)
        {
            // Try to enumerate it as a collection first (ImmutableArray, LystStruct, etc.)
            var enumItems = AuditTryEnumerate(val);
            if (enumItems is not null)
            {
                CheckCollectionItems(enumItems, ownerTypeName, fieldPath,
                    val.GetType().Name, declaredOnly, tProto, findings, depth);
            }
            else if (depth < 3)
            {
                // Plain struct: scan its fields
                var sType = val.GetType();
                for (var cur = sType; cur is not null && cur != typeof(object); cur = cur.BaseType)
                {
                    foreach (var sFi in cur.GetFields(declaredOnly))
                    {
                        try
                        {
                            CheckField(sFi, val, ownerTypeName, $"{fieldPath}.{sFi.Name}",
                                declaredOnly, tProto, findings, depth + 1);
                        }
                        catch { }
                    }
                }
            }
            return;
        }

        // ── Reference-type collection field ──────────────────────────────
        var items = AuditTryEnumerate(val);
        if (items is null) return;

        CheckCollectionItems(items, ownerTypeName, fieldPath,
            val.GetType().Name, declaredOnly, tProto, findings, depth);
    }

    private void CheckCollectionItems(
        List<object> items, string ownerTypeName, string fieldPath, string containerTypeName,
        BindingFlags declaredOnly, Type? tProto,
        List<AuditFinding> findings, int depth)
    {
        int phantomCount = 0;
        string protoTypeName = "?";

        foreach (var item in items)
        {
            if (item is null) continue;
            var iType = item.GetType();

            // Direct phantom
            if (_phantomProtoStubs!.Contains(item))
            {
                phantomCount++;
                protoTypeName = iType.Name;
                continue;
            }

            if (iType.IsValueType && depth < 3)
            {
                // Struct item (e.g. ZoneFilterData, KeyValuePair) — check its ref-type fields
                for (var cur = iType; cur is not null && cur != typeof(object); cur = cur.BaseType)
                {
                    foreach (var sFi in cur.GetFields(declaredOnly))
                    {
                        try
                        {
                            CheckField(sFi, item, ownerTypeName, $"{fieldPath}[*].{sFi.Name}",
                                declaredOnly, tProto, findings, depth + 1);
                        }
                        catch { }
                    }
                }
            }
            else if (!iType.IsValueType && iType != typeof(string))
            {
                // Check KVP key
                try
                {
                    var key = iType.GetProperty("Key")?.GetValue(item);
                    if (key is not null && _phantomProtoStubs.Contains(key))
                    {
                        phantomCount++;
                        protoTypeName = key.GetType().Name;
                    }
                }
                catch { }
                // No BFS expansion — flat scan only
            }
        }

        if (phantomCount > 0)
            findings.Add(new AuditFinding(protoTypeName, ownerTypeName, fieldPath, containerTypeName, phantomCount));
    }

    private static List<object>? AuditTryEnumerate(object val)
    {
        // Don't enumerate strings or arrays of primitives — too large and never have proto refs
        if (val is string) return null;
        if (val is Array arr && (arr.Rank != 1 || arr.Length == 0)) return null;
        if (val is Array arr2 && arr2.Length > 0)
        {
            var et = arr2.GetType().GetElementType();
            if (et is not null && (et.IsPrimitive || et.IsEnum)) return null;
        }

        if (val is System.Collections.IEnumerable col)
        {
            try
            {
                var list = col.Cast<object>().Take(50_000).ToList();
                return list;
            }
            catch { }
        }

        // Struct-based enumerator (ImmutableArray<T>, LystStruct<T>, etc.)
        try
        {
            var getEnum = val.GetType().GetMethod("GetEnumerator");
            if (getEnum is null) return null;
            var en = getEnum.Invoke(val, null);
            if (en is null) return null;
            var et2 = en.GetType();
            var mn = et2.GetMethod("MoveNext");
            var cp = et2.GetProperty("Current");
            if (mn is null || cp is null) return null;
            var result = new List<object>();
            int limit = 50_000;
            while (limit-- > 0 && mn.Invoke(en, null) is true)
            {
                var item = cp.GetValue(en);
                if (item is not null) result.Add(item);
            }
            return result;
        }
        catch { return null; }
    }
}
