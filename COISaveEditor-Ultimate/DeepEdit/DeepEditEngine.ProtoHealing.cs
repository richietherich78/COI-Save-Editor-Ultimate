using System.Reflection;

namespace COISaveEditorUltimate.DeepEdit;

public sealed partial class DeepEditEngine
{
    // ── Proto healing ─────────────────────────────────────────────────────
    //
    // When COIExtended was active, it replaced the primary proto of many vanilla
    // entities (Shipyard, CaptainOffice, SettlementHousingModule …) and goal lists
    // with its own extended proto instances.  Those proto IDs are now phantom stubs
    // because COIExtended is being removed.
    //
    // Rather than stripping those vanilla entities/GoalLists (which loses base-game
    // content), we find the matching vanilla proto in _populatedProtosDb and write
    // it back into the phantom field — healing the object so it loads correctly.

    /// <summary>
    /// Index of all protos registered in <c>_populatedProtosDb</c>, keyed by exact
    /// runtime type and by ID string.  Used to find a vanilla replacement for any
    /// phantom proto stub encountered during stripping.
    /// </summary>
    internal sealed class ProtoHealingLookup
    {
        internal readonly Dictionary<Type, List<object>> ByExactType = new();
        // Secondary index — every base type up to (but not including) Proto.
        // Lets us answer "give me all candidates assignable to BarrierProto" in O(1).
        internal readonly Dictionary<Type, List<object>> ByAssignableType = new();
        internal readonly Dictionary<string, object> ById = new(StringComparer.OrdinalIgnoreCase);

        private readonly FieldInfo? _fiProtoId;
        private readonly FieldInfo? _fiProtoIdValueField;
        private readonly PropertyInfo? _piProtoIdValueProp;

        // Production constructor: uses reflection to read proto ID strings.
        internal ProtoHealingLookup(
            FieldInfo? fiProtoId,
            FieldInfo? fiProtoIdValueField,
            PropertyInfo? piProtoIdValueProp)
        {
            _fiProtoId = fiProtoId;
            _fiProtoIdValueField = fiProtoIdValueField;
            _piProtoIdValueProp = piProtoIdValueProp;
        }

        // Parameterless constructor for unit tests — ID extraction not available.
        internal ProtoHealingLookup() { }

        // Per-concrete-type budget for "no candidate" diagnostic dumps. Keeps the log
        // useful on large saves (15k+ phantom refs) while still proving once per type
        // whether vanilla candidates exist for that proto.
        private readonly Dictionary<Type, int> _candidateLogBudget = new();
        private const int CandidateLogBudgetPerType = 3;
        internal bool ShouldLogCandidatesFor(Type concreteType)
        {
            if (!_candidateLogBudget.TryGetValue(concreteType, out var remaining))
                remaining = CandidateLogBudgetPerType;
            if (remaining <= 0) return false;
            _candidateLogBudget[concreteType] = remaining - 1;
            return true;
        }

        internal string? GetProtoIdString(object proto)
        {
            if (_fiProtoId is not null)
            {
                try
                {
                    var idObj = _fiProtoId.GetValue(proto);
                    if (idObj is not null)
                    {
                        var fromField = ExtractIdString(idObj);
                        if (!string.IsNullOrEmpty(fromField)) return fromField;
                    }
                }
                catch { }
            }

            // Fallback: all Mafi Proto subclasses override ToString() to return their ID string.
            // Also used in unit tests where _fiProtoId is null (parameterless constructor path).
            try
            {
                var s = proto.ToString();
                // Guard against the default Object.ToString() which returns the type full name.
                if (!string.IsNullOrEmpty(s) && s != proto.GetType().FullName && s != proto.GetType().Name)
                    return s;
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Reads the underlying string out of a Proto.ID value. The struct exposes its
        /// payload either via a field or a property (varies by build / decompiler shape),
        /// and ToString() is a reliable last resort because Mafi overrides it to return Value.
        /// </summary>
        internal string? ExtractIdString(object idObj)
        {
            try
            {
                if (_fiProtoIdValueField is not null
                    && _fiProtoIdValueField.GetValue(idObj) is string fromField
                    && !string.IsNullOrEmpty(fromField))
                    return fromField;
            }
            catch { }
            try
            {
                if (_piProtoIdValueProp is not null
                    && _piProtoIdValueProp.GetValue(idObj) is string fromProp
                    && !string.IsNullOrEmpty(fromProp))
                    return fromProp;
            }
            catch { }
            try
            {
                var s = idObj.ToString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
        }
    }

    private ProtoHealingLookup? _protoHealingLookup;

    /// <summary>
    /// Enumerates all protos in the populated ProtosDb and builds a lookup keyed
    /// by exact runtime type (for unambiguous healing) and by proto ID string
    /// (for ID-based partial matching when multiple protos share the same type).
    /// </summary>
    private ProtoHealingLookup? BuildProtoHealingLookup(IProgress<string>? progress)
    {
        if (_populatedProtosDb is null) return null;

        var tProto = AssemblyLoader.FindType("Mafi.Core.Prototypes.Proto");
        if (tProto is null) return null;

        var fiProtoId = tProto.GetField("<Id>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var tProtoID = tProto.GetNestedType("ID", BindingFlags.Public);
        var fiProtoIdValueField = tProtoID?.GetField("Value",
            BindingFlags.Public | BindingFlags.Instance);
        var piProtoIdValueProp = tProtoID?.GetProperty("Value",
            BindingFlags.Public | BindingFlags.Instance);

        var miAll = _populatedProtosDb.GetType()
            .GetMethod("All", BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
        if (miAll is null)
        {
            progress?.Report("  Proto healing: ProtosDb.All() not found — healing disabled.");
            return null;
        }

        var allProtos = (miAll.Invoke(_populatedProtosDb, null)
            as System.Collections.IEnumerable)?
            .Cast<object>().ToList() ?? new List<object>();

        var lookup = new ProtoHealingLookup(fiProtoId, fiProtoIdValueField, piProtoIdValueProp);

        foreach (var proto in allProtos)
        {
            if (proto is null) continue;
            var t = proto.GetType();

            if (!lookup.ByExactType.TryGetValue(t, out var list))
                lookup.ByExactType[t] = list = new List<object>();
            list.Add(proto);

            // Also index every base type up to (but not including) Proto itself,
            // so a phantom typed as BarrierProto can find candidates registered as
            // BarrierProto_Tier1. We deliberately stop before Proto: a bare-Proto
            // bucket would let a stub typed as e.g. ProductProto pull candidates of
            // unrelated types like TerrainDesignationProto, which would then fail
            // the typed cast inside the corresponding SlimIdManager (e.g.
            // ProductsSlimIdManager expects every ManagedProtos[N] to be assignable
            // to ProductProto specifically).
            for (var bt = t.BaseType; bt is not null && bt != typeof(object) && bt != tProto; bt = bt.BaseType)
            {
                if (!lookup.ByAssignableType.TryGetValue(bt, out var baseList))
                    lookup.ByAssignableType[bt] = baseList = new List<object>();
                baseList.Add(proto);
            }

            var idStr = lookup.GetProtoIdString(proto);
            if (!string.IsNullOrEmpty(idStr))
                lookup.ById[idStr] = proto;
        }

        progress?.Report($"  Proto healing lookup: {allProtos.Count} protos across {lookup.ByExactType.Count} exact type(s), {lookup.ByAssignableType.Count} base type(s), {lookup.ById.Count} ID(s).");
        return lookup;
    }

    // ── Instance wrappers ─────────────────────────────────────────────────

    /// <summary>
    /// Scans the entity's primary proto fields (m_proto / m_prototype / backing fields).
    /// If any is a phantom stub, attempts to replace it with a vanilla proto from the
    /// healing lookup.  Returns true if the field was healed.
    /// </summary>
    private bool TryHealEntityPrimaryProto(object entity, BindingFlags allFlags, IProgress<string>? progress)
    {
        if (_protoHealingLookup is null || _phantomProtoStubs is null) return false;
        var entityType = entity.GetType();
        for (var cur = entityType; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var fi in cur.GetFields(allFlags | BindingFlags.DeclaredOnly))
            {
                if (fi.FieldType.IsValueType) continue;
                bool isPrimary = fi.Name.Equals("m_proto", StringComparison.Ordinal)
                    || fi.Name.Equals("m_prototype", StringComparison.OrdinalIgnoreCase)
                    || (fi.Name.StartsWith("<", StringComparison.Ordinal)
                        && fi.Name.EndsWith(">k__BackingField", StringComparison.Ordinal)
                        && fi.Name.IndexOf("Proto", 1, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!isPrimary) continue;
                try
                {
                    var v = fi.GetValue(entity);
                    if (v is null) return false; // null — type unknown, can't infer replacement
                    if (_phantomProtoStubs.Contains(v))
                        return TryHealPhantomField(entity, fi, v, _protoHealingLookup, progress);
                }
                catch { }
            }
        }
        return false;
    }

    /// <summary>
    /// Scans every Proto-typed field of a non-entity object (e.g. GoalsList).
    /// For each phantom field, attempts to heal with a vanilla proto.
    /// Returns true only if at least one field was healed and none failed.
    /// </summary>
    private bool TryHealNonEntityPhantomProto(object item, Type? tProto, BindingFlags allFlags, IProgress<string>? progress)
    {
        if (_protoHealingLookup is null || _phantomProtoStubs is null || tProto is null) return false;
        bool anyHealed = false, anyFailed = false;
        for (var cur = item.GetType(); cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var fi in cur.GetFields(allFlags))
            {
                if (fi.FieldType.IsValueType) continue;
                if (!tProto.IsAssignableFrom(fi.FieldType)) continue;
                try
                {
                    var v = fi.GetValue(item);
                    if (v is not null && _phantomProtoStubs.Contains(v))
                    {
                        if (TryHealPhantomField(item, fi, v, _protoHealingLookup, progress))
                            anyHealed = true;
                        else
                            anyFailed = true;
                    }
                }
                catch { anyFailed = true; }
            }
        }
        return anyHealed && !anyFailed;
    }

    // ── Testable static core ──────────────────────────────────────────────

    /// <summary>
    /// Attempts to replace a phantom proto stub in <paramref name="fi"/> on
    /// <paramref name="obj"/> with the matching vanilla proto from <paramref name="lookup"/>.
    ///
    /// Resolution strategy (tried in order):
    ///   1. Exact ID match in lookup.ById — handles the rare case where the vanilla ID
    ///      is reused by the mod.
    ///   2. Single candidate of the phantom's concrete runtime type in ByExactType.
    ///      For unique-per-game entities (Shipyard, CaptainOffice…) this is unambiguous.
    ///   3. Longest-substring ID match: finds the vanilla proto whose ID is a substring
    ///      of the phantom's COIExtended ID.  Used when multiple protos share the same
    ///      type (e.g. SettlementHousingModule tier variants).
    ///   4. Declared field type fallback: repeats 2 and 3 using fi.FieldType instead of
    ///      the phantom's concrete type — handles cases where the phantom is an
    ///      uninitialized base-type stub.
    ///
    /// Returns true if the field was successfully healed.
    /// </summary>
    internal static bool TryHealPhantomField(
        object obj, FieldInfo fi, object phantomProto,
        ProtoHealingLookup lookup,
        IProgress<string>? progress = null)
    {
        var concreteType = phantomProto.GetType();
        var declaredType = fi.FieldType;
        // GetProtoIdString uses the reflection-extracted <Id>k__BackingField first, then
        // falls back to ToString() (Mafi Proto convention / unit-test compat).
        var phantomIdStr = lookup.GetProtoIdString(phantomProto) ?? "";

        // 1. Exact ID match.
        if (!string.IsNullOrEmpty(phantomIdStr)
            && lookup.ById.TryGetValue(phantomIdStr, out var byIdMatch)
            && declaredType.IsInstanceOfType(byIdMatch))
        {
            // Exact-ID matches are guaranteed shape-compatible (same proto). No guard needed.
            fi.SetValue(obj, byIdMatch);
            progress?.Report($"    Healed {obj.GetType().Name}.{fi.Name} via exact ID '{phantomIdStr}'");
            return true;
        }

        // Try healing with candidates of concrete type first, then declared type.
        foreach (var searchType in new[] { concreteType, declaredType })
        {
            if (!lookup.ByExactType.TryGetValue(searchType, out var candidates)
                || candidates.Count == 0) continue;

            // Verify field assignability when using the declared-type fallback.
            if (searchType == declaredType && !declaredType.IsInstanceOfType(candidates[0])) continue;

            // 2. Single unambiguous candidate — but only if port shapes are compatible.
            // A sole candidate of the same concrete type may still have a different
            // PortsShape (e.g. vanilla Zipper_IoPortShape_Pipe vs. mod's _Pipe_long).
            if (candidates.Count == 1)
            {
                if (!AreProtoShapesCompatible(phantomProto, candidates[0], lookup))
                {
                    progress?.Report($"    Skipping {obj.GetType().Name}.{fi.Name}: sole {searchType.Name} candidate has incompatible PortsShape (phantom='{phantomIdStr}')");
                    continue; // try next searchType or fall through to "cannot heal"
                }
                fi.SetValue(obj, candidates[0]);
                progress?.Report($"    Healed {obj.GetType().Name}.{fi.Name} (sole {searchType.Name} in vanilla)");
                return true;
            }

            // 3. Partial ID match — pick the closest vanilla proto by ID similarity.
            //
            // Tier-aware preference: many phantom IDs are tier-suffixed (e.g. T4, T6,
            // III, IV, "Large", etc.) added by COIExtended. The vanilla game has lower
            // tiers of the same family. We want to substitute the HIGHEST AVAILABLE
            // vanilla tier, not the lowest (otherwise a player's T4 conveyor network
            // silently downgrades to T1, halving throughput → simulation backlog →
            // game lockup). Strategy:
            //   a. Strip the trailing tier marker from the phantom ID to get a "stem".
            //   b. Among candidates whose ID begins with that stem, pick the one with
            //      the LARGEST trailing tier marker (T3 > T2 > T1).
            //   c. Fall back to the original longest-substring rule if no stem-prefix
            //      candidates exist.
            if (!string.IsNullOrEmpty(phantomIdStr))
            {
                object? best = null;

                var stem = StripTierSuffix(phantomIdStr);
                if (!string.IsNullOrEmpty(stem))
                {
                    int bestRank = int.MinValue;
                    foreach (var candidate in candidates)
                    {
                        var candidateId = lookup.GetProtoIdString(candidate) ?? "";
                        if (string.IsNullOrEmpty(candidateId)) continue;
                        if (!candidateId.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
                        // Reject candidates whose extra suffix is alphanumeric content
                        // *other* than a recognised tier marker (avoids matching e.g.
                        // 'LooseMaterialConveyorOnPiers' for stem 'LooseMaterialConveyor').
                        var extra = candidateId.Substring(stem.Length);
                        int rank = TierRank(extra);
                        if (rank == int.MinValue) continue;
                        // Shape-compatibility guard: reject if PortsShape IDs differ.
                        if (!AreProtoShapesCompatible(phantomProto, candidate, lookup)) continue;
                        if (rank > bestRank)
                        {
                            best = candidate;
                            bestRank = rank;
                        }
                    }
                }

                // Fallback: original longest-substring rule.
                if (best is null)
                {
                    int bestLen = 0;
                    foreach (var candidate in candidates)
                    {
                        var candidateId = lookup.GetProtoIdString(candidate) ?? "";
                        if (string.IsNullOrEmpty(candidateId)) continue;
                        if (phantomIdStr.Contains(candidateId, StringComparison.OrdinalIgnoreCase)
                            && candidateId.Length > bestLen)
                        {
                            // Shape-compatibility guard.
                            if (!AreProtoShapesCompatible(phantomProto, candidate, lookup)) continue;
                            best = candidate;
                            bestLen = candidateId.Length;
                        }
                    }
                }

                if (best is not null)
                {
                    fi.SetValue(obj, best);
                    progress?.Report($"    Healed {obj.GetType().Name}.{fi.Name} via partial match '{lookup.GetProtoIdString(best)}' ← '{phantomIdStr}'");
                    return true;
                }
            }
        }

        // 5. Assignable-type search: when ByExactType[BarrierProto] is empty because every
        // vanilla proto is registered as a subclass (BarrierProtoTier1, …), fall back to the
        // pre-built ByAssignableType index keyed on the declared/concrete base type.
        foreach (var searchType in new[] { concreteType, declaredType })
        {
            if (!lookup.ByAssignableType.TryGetValue(searchType, out var assignable)
                || assignable.Count == 0) continue;

            // Filter to candidates actually assignable to the field type (defensive).
            var viable = assignable.Where(c => declaredType.IsInstanceOfType(c)).ToList();
            if (viable.Count == 0) continue;

            if (viable.Count == 1)
            {
                if (!AreProtoShapesCompatible(phantomProto, viable[0], lookup))
                {
                    progress?.Report($"    Skipping {obj.GetType().Name}.{fi.Name}: sole assignable-to-{searchType.Name} candidate has incompatible PortsShape (phantom='{phantomIdStr}')");
                    continue;
                }
                fi.SetValue(obj, viable[0]);
                progress?.Report($"    Healed {obj.GetType().Name}.{fi.Name} (sole assignable to {searchType.Name})");
                return true;
            }

            if (!string.IsNullOrEmpty(phantomIdStr))
            {
                object? best = null;
                int bestLen = 0;
                foreach (var candidate in viable)
                {
                    var candidateId = lookup.GetProtoIdString(candidate) ?? "";
                    if (string.IsNullOrEmpty(candidateId)) continue;
                    if (phantomIdStr.Contains(candidateId, StringComparison.OrdinalIgnoreCase)
                        && candidateId.Length > bestLen)
                    {
                        // Shape-compatibility guard.
                        if (!AreProtoShapesCompatible(phantomProto, candidate, lookup)) continue;
                        best = candidate;
                        bestLen = candidateId.Length;
                    }
                }
                if (best is not null)
                {
                    fi.SetValue(obj, best);
                    progress?.Report($"    Healed {obj.GetType().Name}.{fi.Name} via assignable+partial '{lookup.GetProtoIdString(best)}' ← '{phantomIdStr}'");
                    return true;
                }
            }
        }

        progress?.Report($"    Cannot heal {obj.GetType().Name}.{fi.Name}: no vanilla match for {concreteType.Name}" +
            (string.IsNullOrEmpty(phantomIdStr) ? "" : $", phantom ID='{phantomIdStr}'"));

        // Diagnostic: log up to a few sample vanilla candidate IDs for this proto type
        // the first few times we see this concrete type fail. This lets you tell at a
        // glance whether the phantom is mod-only content (no plausible vanilla) or whether
        // the mod merely renamed/prepended a vanilla ID we should match.
        if (lookup.ShouldLogCandidatesFor(concreteType))
        {
            var sampleIds = (lookup.ByExactType.TryGetValue(concreteType, out var exact) ? exact : null)
                ?? (lookup.ByAssignableType.TryGetValue(concreteType, out var asgn) ? asgn : null);
            if (sampleIds is { Count: > 0 })
            {
                var ids = sampleIds
                    .Select(c => lookup.GetProtoIdString(c) ?? "<no-id>")
                    .Take(8)
                    .ToList();
                progress?.Report($"      Vanilla {concreteType.Name} candidates ({sampleIds.Count} total, showing {ids.Count}): {string.Join(", ", ids)}");
            }
            else
            {
                progress?.Report($"      No vanilla {concreteType.Name} (or any assignable subclass) registered — phantom is mod-only content.");
            }
        }
        return false;
    }

    /// <summary>
    /// Strips a recognised tier suffix from a proto ID so the remaining "stem" can be
    /// used to find sibling vanilla protos (e.g. <c>"LooseMaterialConveyorT4"</c> →
    /// <c>"LooseMaterialConveyor"</c>; <c>"FishingDockIII"</c> → <c>"FishingDock"</c>;
    /// <c>"LargeTruckH"</c> → <c>"LargeTruck"</c>).
    /// <para/>
    /// Returns the input unchanged if no tier suffix is recognised. Suffixes recognised
    /// are: <c>T1..T9</c>, <c>I..VIII</c> (Roman numerals up to 8), and the single-letter
    /// variant tags <c>D</c>, <c>H</c>, <c>S</c>, <c>X</c> (only when preceded by a
    /// lowercase letter, to avoid trimming legitimate ID endings like <c>...Coal</c>).
    /// </summary>
    private static string StripTierSuffix(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;

        // Tier markers: TN (N=1..9).
        if (id.Length >= 3 && id[^2] == 'T' && id[^1] is >= '1' and <= '9')
            return id.Substring(0, id.Length - 2);

        // Roman-numeral suffixes (II..VIII). Walk back from end while we see I/V.
        int romanLen = 0;
        for (int i = id.Length - 1; i >= 0; i--)
        {
            if (id[i] == 'I' || id[i] == 'V') romanLen++;
            else break;
        }
        if (romanLen >= 1 && romanLen <= 4
            && IsValidRoman(id.AsSpan(id.Length - romanLen))
            && id.Length - romanLen > 0
            && char.IsLetter(id[id.Length - romanLen - 1])
            && char.IsLower(id[id.Length - romanLen - 1]))
        {
            return id.Substring(0, id.Length - romanLen);
        }

        // Single-letter variant tag (Diesel/Hydrogen/Steam/X) preceded by lowercase.
        if (id.Length >= 2)
        {
            char last = id[^1];
            char prev = id[^2];
            if ((last == 'D' || last == 'H' || last == 'S' || last == 'X')
                && char.IsLetter(prev) && char.IsLower(prev))
                return id.Substring(0, id.Length - 1);
        }

        return id;
    }

    private static bool IsValidRoman(ReadOnlySpan<char> s)
    {
        // Accepts I, II, III, IV, V, VI, VII, VIII.
        return s.SequenceEqual("I") || s.SequenceEqual("II") || s.SequenceEqual("III")
            || s.SequenceEqual("IV") || s.SequenceEqual("V") || s.SequenceEqual("VI")
            || s.SequenceEqual("VII") || s.SequenceEqual("VIII");
    }

    // ── Port-shape compatibility guard ───────────────────────────────────
    //
    // Some proto types (TransportProto, ZipperProto) have a PortsShape field whose
    // value encodes the physical connector geometry. Healed entities whose saved
    // geometry was built for proto A can't run initSelf/initAfterLoad with proto B if
    // the PortsShape IDs differ — the result is NullReferenceException or
    // IndexOutOfRangeException deep in trajectory / electricity-consumer init.
    //
    // The guard reads PortsShape (by name, public field) from both phantom and
    // candidate, extracts the ID string via the same path used for proto IDs, and
    // rejects the candidate when the IDs differ.  If neither proto exposes a
    // PortsShape field we return true (compatible) so no existing heal regressions.

    // Cache the PortsShape FieldInfo per proto type to avoid repeated GetField calls
    // over a save with 1 700+ transports.
    private static readonly Dictionary<Type, FieldInfo?> s_portsShapeFieldCache = new();

    private static FieldInfo? GetPortsShapeField(Type protoType)
    {
        if (s_portsShapeFieldCache.TryGetValue(protoType, out var fi)) return fi;
        // Walk up the type hierarchy; PortsShape is declared directly on the concrete proto class.
        for (var t = protoType; t is not null && t != typeof(object); t = t.BaseType)
        {
            fi = t.GetField("PortsShape",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (fi is not null) break;
        }
        s_portsShapeFieldCache[protoType] = fi;
        return fi;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="candidate"/> is safe to substitute for
    /// <paramref name="phantom"/> from a port-shape perspective.
    /// <para/>
    /// If either proto lacks a <c>PortsShape</c> field we conservatively return <c>true</c>
    /// (no shape constraint known → don't block the heal).  When both expose <c>PortsShape</c>
    /// we compare the underlying ID strings; equal IDs → compatible, different IDs → reject.
    /// </summary>
    internal static bool AreProtoShapesCompatible(
        object phantom, object candidate, ProtoHealingLookup lookup)
    {
        var phantomShapeFi    = GetPortsShapeField(phantom.GetType());
        var candidateShapeFi  = GetPortsShapeField(candidate.GetType());

        // If neither side has PortsShape, no constraint — allow.
        if (phantomShapeFi is null && candidateShapeFi is null) return true;

        try
        {
            var phantomShape   = phantomShapeFi?.GetValue(phantom);
            var candidateShape = candidateShapeFi?.GetValue(candidate);

            // If one side has the field but we can't read it, be conservative: allow.
            if (phantomShape is null || candidateShape is null) return true;

            // Extract ID strings from the IoPortShapeProto objects.
            // Primary: GetProtoIdString reads the <Id>k__BackingField proto ID struct then
            //          calls ExtractIdString — works when the lookup has reflection pointers.
            // Fallback: ToString() — all Mafi protos override it to return their ID string,
            //           and it also works in unit tests where _fiProtoId is null.
            var phantomId   = lookup.GetProtoIdString(phantomShape)
                              ?? phantomShape.ToString();
            var candidateId = lookup.GetProtoIdString(candidateShape)
                              ?? candidateShape.ToString();

            if (string.IsNullOrEmpty(phantomId) || string.IsNullOrEmpty(candidateId)) return true;

            return string.Equals(phantomId, candidateId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true; // reflection failure → be permissive
        }
    }

    /// <summary>
    /// Returns a numeric rank for a tier-suffix string so candidates can be compared.
    /// Higher rank = higher tier. Returns <see cref="int.MinValue"/> if the suffix is
    /// not a recognised tier marker (so the candidate is rejected as a sibling).
    /// <para/>
    /// Empty suffix (the bare stem itself, e.g. <c>"LooseMaterialConveyor"</c>) ranks 1
    /// — it represents the implicit T1 / base variant and IS a valid sibling.
    /// </summary>
    private static int TierRank(string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return 1; // base/T1
        // TN
        if (suffix.Length == 2 && suffix[0] == 'T' && suffix[1] is >= '1' and <= '9')
            return suffix[1] - '0';
        // Roman
        return suffix switch
        {
            "I"    => 1,
            "II"   => 2,
            "III"  => 3,
            "IV"   => 4,
            "V"    => 5,
            "VI"   => 6,
            "VII"  => 7,
            "VIII" => 8,
            _      => int.MinValue,
        };
    }
}
