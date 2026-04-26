// SPDX-License-Identifier: see project root.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace COISaveEditorUltimate.DeepEdit;

/// <summary>
/// Scans a re-serialised save payload for type-name strings that reference assemblies
/// the game cannot load (i.e. the user-removed mod assemblies). Any hit means a future
/// game load will fail with <c>CorruptedSaveException: Failed to load type from '…'</c>
/// or <c>Failed cast loaded proto '__PHANTOM__FAILED_TO_LOAD__…' to '…'</c>.
///
/// <para/>
/// Mafi <c>BlobWriter.WriteTypeNameAsStrNoRef</c> writes either <c>Type.FullName</c>
/// or <c>Type.AssemblyQualifiedName</c>. AQNs include the substring
/// <c>", &lt;assemblyName&gt;, Version="</c>. We search the payload for that substring
/// per removed assembly, then walk backwards to find the start of the enclosing
/// length-prefixed UTF-8 string so we can surface the offending type name in full
/// for diagnostics.
/// </summary>
public static class SaveOutputValidator
{
    public sealed class Violation
    {
        public required string AssemblySimpleName { get; init; }
        public required string TypeStringSnippet  { get; init; }
        public required int    FirstOffset        { get; init; }
        public          int    OccurrenceCount    { get; set; }
        /// <summary>
        /// Best-effort identification of the closest preceding Mafi/game type-name string
        /// in the payload. For phantom-proto-ID hits this usually identifies the owning
        /// entity or struct (e.g. <c>Machine+MachineOutputBuffer</c>) which tells us
        /// which scrub pass needs improvement.
        /// </summary>
        public string? NearestPrecedingType { get; set; }

        /// <summary>
        /// Hex+ASCII dump of the few hundred bytes immediately preceding the hit offset.
        /// Lets us identify the parent class whose serializer wrote this entity inline,
        /// by reading the printable type-name strings BlobWriter emits before each
        /// class instance. Populated only for the first occurrence of each violation.
        /// </summary>
        public string? PrecedingBytesDump { get; set; }
    }

    public sealed class Report
    {
        public List<Violation> Violations { get; } = new();
        public int TotalOccurrences { get; set; }
        public bool IsClean => Violations.Count == 0;
    }

    /// <summary>
    /// Scan <paramref name="payload"/> for two categories of game-load-fatal content:
    /// <list type="number">
    ///   <item>Type-name strings that reference removed-mod assemblies (the game's
    ///   <c>BlobReader.ReadType</c> will throw <c>CorruptedSaveException: Failed to load
    ///   type from '…'</c>).</item>
    ///   <item>Reassigned phantom proto IDs of the form <c>__phantom_NNN</c> (these
    ///   reach the game's <c>ProtosDb</c> as unresolvable, get wrapped in
    ///   <c>InvalidProto</c>, and crash any typed proto deserializer with
    ///   <c>Failed cast loaded proto '__PHANTOM__FAILED_TO_LOAD____phantom_NNN' to '…'</c>).</item>
    /// </list>
    /// Identical findings are coalesced and counted.
    /// </summary>
    public static Report Validate(
        byte[] payload,
        IReadOnlyCollection<string> removedModSimpleNames)
    {
        var report = new Report();
        if (payload is null || payload.Length == 0) return report;

        // Category 1: removed-mod AQN substrings.
        if (removedModSimpleNames is not null && removedModSimpleNames.Count > 0)
        {
            // The unique-per-distinct-type bucket so we coalesce e.g. 80 occurrences of
            // "COIExtended.Core.…FishFarm, COIExtended.Core, Version=…" into one entry.
            var byKey = new Dictionary<string, Violation>(StringComparer.Ordinal);

            foreach (var asmName in removedModSimpleNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Pattern that appears inside any AssemblyQualifiedName referencing this assembly.
                // Mafi writes ", AsmName, Version=" so we search for that exact byte sequence.
                byte[] needle = Encoding.UTF8.GetBytes(", " + asmName + ", Version=");
                int searchFrom = 0;
                while (searchFrom + needle.Length < payload.Length)
                {
                    int idx = payload.AsSpan(searchFrom).IndexOf(needle);
                    if (idx < 0) break;
                    int absIdx = searchFrom + idx;

                    // Extract a printable type-string snippet for diagnostics.
                    string snippet = ExtractStringAround(payload, absIdx);
                    string key = asmName + "|" + snippet;
                    if (!byKey.TryGetValue(key, out var v))
                    {
                        v = new Violation
                        {
                            AssemblySimpleName   = asmName,
                            TypeStringSnippet    = snippet,
                            FirstOffset          = absIdx,
                            OccurrenceCount      = 0,
                            NearestPrecedingType = FindNearestPrecedingMafiTypeName(payload, absIdx),
                            PrecedingBytesDump   = DumpPrecedingBytes(payload, absIdx, 1024),
                        };
                        byKey.Add(key, v);
                        report.Violations.Add(v);
                    }
                    v.OccurrenceCount++;
                    report.TotalOccurrences++;
                    searchFrom = absIdx + needle.Length;
                }
            }
        }

        // Category 2: __phantom_NNN proto IDs. These exist regardless of which mod was
        // removed: NullifyPhantomProtoIds reassigns mod-only proto IDs to placeholders
        // so the SlimIdManager can register them, but any save-graph reference to a
        // proto by ID will crash deserialization when the game can't resolve the placeholder.
        // Coalesce by exact ID so the report shows e.g. "__phantom_258 × 14".
        {
            byte[] needle = "__phantom_"u8.ToArray();
            var byId = new Dictionary<string, Violation>(StringComparer.Ordinal);
            int searchFrom = 0;
            while (searchFrom + needle.Length < payload.Length)
            {
                int idx = payload.AsSpan(searchFrom).IndexOf(needle);
                if (idx < 0) break;
                int absIdx = searchFrom + idx;

                // Extract just the "__phantom_NNN" run (digits after the prefix).
                int end = absIdx + needle.Length;
                while (end < payload.Length && payload[end] >= (byte)'0' && payload[end] <= (byte)'9')
                    end++;
                string id = Encoding.ASCII.GetString(payload, absIdx, end - absIdx);
                if (!byId.TryGetValue(id, out var v))
                {
                    v = new Violation
                    {
                        AssemblySimpleName = "(phantom proto ID)",
                        TypeStringSnippet  = id,
                        FirstOffset        = absIdx,
                        OccurrenceCount    = 0,
                        NearestPrecedingType = FindNearestPrecedingMafiTypeName(payload, absIdx),
                    };
                    byId.Add(id, v);
                    report.Violations.Add(v);
                }
                v.OccurrenceCount++;
                report.TotalOccurrences++;
                searchFrom = end;
            }
        }

        return report;
    }

    /// <summary>
    /// Best-effort extraction of a printable type-name snippet around an offset known to
    /// land inside an AssemblyQualifiedName. Walks backwards while bytes are printable
    /// ASCII (typical for AQNs) and forwards until a non-printable terminator is hit.
    /// Caps the returned snippet length so a corrupt buffer can't blow up the log.
    /// </summary>
    private static string ExtractStringAround(byte[] payload, int hitOffset)
    {
        const int MaxBack = 256;
        const int MaxFwd  = 256;

        int start = hitOffset;
        for (int i = 0; i < MaxBack && start - 1 >= 0; i++)
        {
            byte b = payload[start - 1];
            if (!IsPrintableAqnByte(b)) break;
            start--;
        }
        int end = hitOffset;
        for (int i = 0; i < MaxFwd && end < payload.Length; i++)
        {
            byte b = payload[end];
            if (!IsPrintableAqnByte(b)) break;
            end++;
        }
        int len = end - start;
        if (len <= 0) return "<unreadable>";
        try { return Encoding.UTF8.GetString(payload, start, len); }
        catch { return "<unreadable>"; }
    }

    private static bool IsPrintableAqnByte(byte b)
    {
        // AQNs use letters, digits, '.', ',', '=', ' ', '`', '[', ']', '+', '-', and PublicKeyToken hex.
        // Anything outside printable ASCII (< 0x20 or > 0x7E) is treated as a string boundary.
        return b >= 0x20 && b <= 0x7E;
    }

    /// <summary>
    /// Produces a compact dump of every printable-ASCII run of length ≥ 6 in the
    /// <paramref name="windowBytes"/> bytes immediately preceding <paramref name="hitOffset"/>.
    /// Each run is emitted on one line as <c>[absoluteOffset] runText</c>. Runs longer than
    /// 200 chars are truncated with an ellipsis.
    /// <para/>
    /// Used to surface, in the validator log, every type-name string the BlobWriter wrote
    /// before the AQN that triggered the violation. The chain of preceding type names is
    /// the most direct evidence of which parent serializer pulled the stripped entity into
    /// the binary, even when no resolver-reachable field holds a ref to that entity.
    /// </summary>
    private static string DumpPrecedingBytes(byte[] payload, int hitOffset, int windowBytes)
    {
        int from = Math.Max(0, hitOffset - windowBytes);
        int to   = hitOffset;
        var sb = new StringBuilder();

        int runStart = -1;
        for (int i = from; i <= to; i++)
        {
            bool atBoundary = i == to;
            byte b = atBoundary ? (byte)0 : payload[i];
            bool printable = !atBoundary && IsPrintableAqnByte(b);

            if (printable)
            {
                if (runStart < 0) runStart = i;
            }
            else
            {
                if (runStart >= 0)
                {
                    int runLen = i - runStart;
                    if (runLen >= 6)
                    {
                        string text = Encoding.ASCII.GetString(payload, runStart, runLen);
                        if (text.Length > 200) text = text.Substring(0, 200) + "…";
                        sb.Append("        [0x").Append(runStart.ToString("X")).Append("] ").AppendLine(text);
                    }
                    runStart = -1;
                }
            }
        }

        return sb.Length == 0 ? "        (no printable runs ≥ 6 chars in preceding window)" : sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Walks backwards from <paramref name="hitOffset"/> looking for the most recent
    /// type-name string written by <c>BlobWriter.WriteTypeNameAsStrNoRef</c>. Returns
    /// the type name only (the part before <c>, AsmName, Version=…</c>), or null if
    /// no plausible candidate is found.
    /// <para/>
    /// The scan looks for prefixes of all kept-game/mod namespaces (Mafi.*, COIExtended.*,
    /// etc.). It explicitly rejects matches that appear immediately after <c>", "</c>,
    /// because that pattern marks the assembly-name segment of an AQN — not a type name.
    /// <para/>
    /// Heuristic, not exact: BlobWriter writes type strings interleaved with object data,
    /// so "the nearest preceding type name" is usually the owning entity/struct of the data
    /// at <paramref name="hitOffset"/>, but not guaranteed. Good enough to identify which
    /// scrub pass needs to handle the location.
    /// </summary>
    private static string? FindNearestPrecedingMafiTypeName(byte[] payload, int hitOffset)
    {
        const int SearchWindow = 16 * 1024;        // Scan up to 16 KB backwards.
        const int MinRunLength = 8;                // "Mafi.X.Y" minimum.
        int from = Math.Max(0, hitOffset - SearchWindow);
        var window = payload.AsSpan(from, hitOffset - from);

        // Candidate type-name prefixes from kept assemblies. Mafi covers vanilla & DLCs;
        // we also include common modder roots so this still works if a mod's *type names*
        // appear in the save (e.g. when the mod is kept and its types are valid loadable).
        ReadOnlySpan<byte> n1 = "Mafi."u8;
        ReadOnlySpan<byte> n2 = "COIExtended."u8;

        // Collect all candidate match offsets (rightmost-first in result).
        // We want the closest valid type-name occurrence to hitOffset.
        int bestStart = -1;
        int bestEnd   = -1;

        ScanForType(window, n1, ref bestStart, ref bestEnd);
        ScanForType(window, n2, ref bestStart, ref bestEnd);

        if (bestStart < 0) return null;
        int absStart = from + bestStart;
        int absEnd   = from + bestEnd;
        int len = absEnd - absStart;
        if (len < MinRunLength) return null;
        try { return Encoding.ASCII.GetString(payload, absStart, len); }
        catch { return null; }

        // For each occurrence of `prefix` in `win`, check that it does NOT immediately
        // follow ", " (which would mean it's the assembly name part of an AQN, not a type).
        // Update best{Start,End} to the rightmost qualifying match.
        static void ScanForType(ReadOnlySpan<byte> win, ReadOnlySpan<byte> prefix,
                                ref int bestStart, ref int bestEnd)
        {
            int searchFrom = 0;
            while (searchFrom + prefix.Length <= win.Length)
            {
                int idx = win.Slice(searchFrom).IndexOf(prefix);
                if (idx < 0) break;
                int hitStart = searchFrom + idx;
                searchFrom = hitStart + prefix.Length;

                // Reject if preceded by ", " (it's the assembly-name segment of an AQN).
                if (hitStart >= 2
                    && win[hitStart - 2] == (byte)','
                    && win[hitStart - 1] == (byte)' ')
                    continue;

                // Read the printable-ASCII run forward, stopping at first ',' (AQN separator)
                // or any non-printable byte (BlobWriter string boundary).
                int runEnd = hitStart;
                while (runEnd < win.Length && IsPrintableAqnByte(win[runEnd]))
                {
                    if (win[runEnd] == (byte)',') break;
                    runEnd++;
                }
                int runLen = runEnd - hitStart;
                if (runLen < 4) continue;

                // Prefer the latest (closest to hitOffset) qualifying match.
                if (hitStart > bestStart)
                {
                    bestStart = hitStart;
                    bestEnd   = runEnd;
                }
            }
        }
    }
}
