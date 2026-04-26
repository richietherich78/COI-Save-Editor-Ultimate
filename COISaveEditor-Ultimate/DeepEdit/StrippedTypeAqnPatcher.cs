using System.Text;

namespace COISaveEditorUltimate.DeepEdit;

/// <summary>
/// Last-line-of-defence post-serialisation patcher: scans the produced resolver
/// payload for every AssemblyQualifiedName string that references a removed-mod
/// assembly and overwrites it in place with a benign, same-byte-length AQN that
/// the game's loader can resolve without crashing.
/// <para/>
/// Why this exists: even with full reflection-based scrubbing of every resolver
/// collection, every reachable EventBase callback list, every <c>Lyst&lt;Type&gt;</c>,
/// and every <c>Dict&lt;Type, *&gt;</c> entry, two AQNs from removed mods
/// (SmartZipper, CargoShipDrydock) consistently survived in the binary. The
/// holders are reachable only through <c>BlobWriter</c>'s internal write queue
/// during serialisation — not through any field-walkable graph from the resolver
/// roots. The game's loader confirms this by failing on
/// <c>Lyst`1[System.Type].ToString() threw NullReferenceException</c> with the
/// stripped-mod AQN as the failed type lookup.
/// <para/>
/// Mafi's <c>BlobWriter.WriteTypeNameAsStrNoRef</c> writes the AQN via
/// <c>BinaryWriter.Write(string)</c>, which encodes a <c>System.IO</c> 7-bit
/// length prefix followed by UTF-8 bytes. We use the same encoding to find each
/// occurrence and rewrite it. The replacement AQN is chosen to:
///  - Be a real, resolvable type the game's loader will accept
///    (<c>System.Object</c>, in <c>mscorlib</c>, the version Mafi ships with).
///  - Match the original AQN's exact UTF-8 byte length so all subsequent offsets
///    in the binary stream remain valid (no shift in any reader-visible position).
///  - Keep the same 7-bit length-prefix bytes so the surrounding stream framing
///    is byte-identical.
/// <para/>
/// The replacement type is interchangeable for all consumers: when a
/// <c>CallbackSaveData.DeclaringType</c> resolves to <c>System.Object</c>, the
/// callback simply fails to find its method and gets dropped on load (Mafi's
/// <c>EventBase</c> already handles this via its <c>Log.Error</c> path), instead
/// of throwing <c>CorruptedSaveException</c>.
/// </summary>
internal static class StrippedTypeAqnPatcher
{
    /// <summary>
    /// Scans <paramref name="payload"/> for AQNs referencing any assembly in
    /// <paramref name="removedAsmNames"/> and overwrites each with a benign,
    /// same-byte-length AQN. Returns the number of patched occurrences.
    /// </summary>
    public static int Patch(byte[] payload, IReadOnlyCollection<string> removedAsmNames, IProgress<string>? progress)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(removedAsmNames);
        if (payload.Length == 0 || removedAsmNames.Count == 0) return 0;

        int patched = 0;
        foreach (var asmName in removedAsmNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Mafi writes ", AsmName, Version=" inside every AQN that references this assembly.
            // The needle anchors us inside the AQN body; we then walk left to find the 7-bit
            // length prefix that BinaryWriter.Write(string) emitted right before the bytes.
            byte[] needle = Encoding.UTF8.GetBytes(", " + asmName + ", Version=");
            int searchFrom = 0;
            while (searchFrom + needle.Length < payload.Length)
            {
                int idx = payload.AsSpan(searchFrom).IndexOf(needle);
                if (idx < 0) break;
                int absIdx = searchFrom + idx;

                if (TryPatchAtAqnHit(payload, absIdx, out int newSearchFrom, out string original, out string replacement))
                {
                    patched++;
                    if (patched <= 20)
                    {
                        progress?.Report($"  [AqnPatch] @0x{absIdx:X}  {Truncate(original, 200)}  →  {Truncate(replacement, 200)}");
                    }
                    searchFrom = newSearchFrom;
                }
                else
                {
                    // Couldn't decode the surrounding string framing — skip ahead past this needle
                    // to avoid an infinite loop. Surfacing a warning so we can investigate later.
                    progress?.Report($"  [AqnPatch] WARNING: needle for '{asmName}' at offset 0x{absIdx:X} did not have a decodable 7-bit length prefix; skipped.");
                    searchFrom = absIdx + needle.Length;
                }
            }
        }

        progress?.Report($"  [AqnPatch] Patched {patched} stripped-mod AQN occurrence(s) in produced payload.");
        return patched;
    }

    /// <summary>
    /// Decodes the BinaryWriter 7-bit length prefix immediately preceding the AQN
    /// hit at <paramref name="needleHit"/>, then overwrites the entire string
    /// (length prefix + bytes) with a same-byte-length replacement.
    /// <para/>
    /// On entry: <paramref name="needleHit"/> points at a ", AsmName, Version=" run
    /// inside an AQN. We scan backwards a bounded distance for the 7-bit length
    /// prefix that, when decoded, equals the byte distance from the prefix end to
    /// some plausible terminator.
    /// <para/>
    /// CRITICAL: every byte inside an ASCII AQN is in 0x20-0x7E and therefore has
    /// the high bit clear, which means EVERY byte position inside the AQN body
    /// looks like a valid 1-byte 7-bit length prefix. So we cannot just accept
    /// the first candidate that decodes; we must find the EARLIEST candidate
    /// (smallest body-start position) AND require that the byte immediately
    /// before the prefix is NOT itself printable ASCII (otherwise the candidate
    /// is mid-string, not a real string boundary).
    /// </summary>
    private static bool TryPatchAtAqnHit(
        byte[] payload, int needleHit,
        out int searchContinueFrom,
        out string originalAqn,
        out string replacementAqn)
    {
        searchContinueFrom = needleHit + 1;
        originalAqn = string.Empty;
        replacementAqn = string.Empty;

        const int MaxAqnLength = 512;
        const int MinAqnLength = 32;
        int searchStart = Math.Max(0, needleHit - MaxAqnLength);

        // Walk EARLIEST-FIRST so we land on the real start of the AQN string,
        // not somewhere mid-string where a printable byte happens to look like
        // a valid 1-byte 7-bit length prefix.
        for (int candidateBodyStart = searchStart; candidateBodyStart <= needleHit; candidateBodyStart++)
        {
            if (!TryReadBackward7BitPrefix(payload, candidateBodyStart, out int prefixStart, out int decodedLen))
                continue;

            if (decodedLen < MinAqnLength || decodedLen > MaxAqnLength) continue;

            int bodyEnd = candidateBodyStart + decodedLen;
            if (bodyEnd > payload.Length) continue;

            // The needle MUST lie inside this body, otherwise this isn't our string.
            if (needleHit < candidateBodyStart || needleHit + 1 > bodyEnd) continue;

            // BOUNDARY CHECK: the byte right before the prefix must NOT be a printable
            // ASCII character. If it is, we're sitting mid-string in some larger AQN
            // and this "prefix" is just a coincidental low-byte of that AQN's body.
            if (prefixStart > 0)
            {
                byte before = payload[prefixStart - 1];
                if (before >= 0x20 && before <= 0x7E) continue;
            }

            // Body must be entirely printable ASCII (typical AQN content).
            bool allPrintable = true;
            for (int i = candidateBodyStart; i < bodyEnd; i++)
            {
                byte b = payload[i];
                if (b < 0x20 || b > 0x7E) { allPrintable = false; break; }
            }
            if (!allPrintable) continue;

            // BOUNDARY CHECK: the byte right after the body must also not be printable
            // ASCII (otherwise we're truncating a longer string mid-way). The very next
            // byte in the stream after a Mafi string is some framing token (often a
            // small integer or another length prefix), almost never another AQN byte.
            if (bodyEnd < payload.Length)
            {
                byte after = payload[bodyEnd];
                if (after >= 0x20 && after <= 0x7E)
                {
                    // Allow if the post-body byte is a non-AQN-typical printable
                    // (e.g. '\0' is non-printable). For safety, only accept if next
                    // byte is clearly framing (high bit set, or low control byte).
                    continue;
                }
            }

            // Found the real start of the AQN. Overwrite in place.
            originalAqn = Encoding.ASCII.GetString(payload, candidateBodyStart, decodedLen);
            replacementAqn = BuildBenignAqn(decodedLen);
            if (replacementAqn.Length != decodedLen) return false;

            byte[] replacementBytes = Encoding.ASCII.GetBytes(replacementAqn);
            Array.Copy(replacementBytes, 0, payload, candidateBodyStart, decodedLen);

            searchContinueFrom = bodyEnd;
            return true;
        }

        return false;
    }

    /// <summary>
    /// BinaryWriter encodes string lengths with a 7-bit varint: low 7 bits per
    /// byte, high bit set on continuation. Maximum 5 bytes for an Int32. This
    /// reads such a prefix that ENDS at byte index <paramref name="endExclusive"/>
    /// (i.e. <paramref name="endExclusive"/> is the first byte of the body).
    /// </summary>
    private static bool TryReadBackward7BitPrefix(byte[] payload, int endExclusive, out int prefixStart, out int decodedLen)
    {
        prefixStart = -1;
        decodedLen = 0;

        // The terminator byte (highest position in the prefix) has the continuation
        // bit CLEAR. So the byte at endExclusive-1 must have its high bit = 0.
        if (endExclusive < 1 || endExclusive > payload.Length) return false;
        int last = endExclusive - 1;
        if ((payload[last] & 0x80) != 0) return false;

        // Walk backwards collecting continuation bytes (high bit set).
        int first = last;
        for (int i = 1; i < 5; i++)
        {
            int probe = last - i;
            if (probe < 0) break;
            if ((payload[probe] & 0x80) == 0) break;
            first = probe;
        }

        // Decode little-endian 7-bit varint from first..last.
        int value = 0;
        int shift = 0;
        for (int i = first; i <= last; i++)
        {
            value |= (payload[i] & 0x7F) << shift;
            shift += 7;
            if (shift > 35) return false;
        }

        prefixStart = first;
        decodedLen = value;
        return value > 0;
    }

    /// <summary>
    /// Builds an AQN string of EXACTLY <paramref name="byteLength"/> ASCII bytes
    /// that the game's loader will resolve to <see cref="object"/>. We start
    /// with the canonical mscorlib <c>System.Object</c> AQN and, if it's shorter
    /// than the target, pad the namespace component with neutral characters that
    /// don't change which type the loader resolves.
    /// <para/>
    /// The Mafi loader uses <c>Type.GetType(aqn)</c>. For mscorlib types it
    /// accepts both the full AQN with version and the bare type name. We use
    /// the full AQN form so the loader has zero ambiguity, and we pad by
    /// repeating the <c>Version</c> assignment with extra spaces (which the
    /// type-name parser tolerates).
    /// </summary>
    private static string BuildBenignAqn(int byteLength)
    {
        // Canonical AQN for System.Object in the .NET-Framework-style mscorlib that
        // Unity Mono ships with (matches every other mscorlib AQN already in the file,
        // e.g. "System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089").
        const string Base = "System.Object, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

        if (byteLength == Base.Length) return Base;
        if (byteLength > Base.Length)
        {
            // Pad with extra spaces inside the Culture= field. .NET's AssemblyName
            // parser tolerates surrounding whitespace around field values.
            int extra = byteLength - Base.Length;
            const string anchor = "Culture=neutral";
            int idx = Base.IndexOf(anchor, StringComparison.Ordinal);
            return Base.Substring(0, idx) + new string(' ', extra) + Base.Substring(idx);
        }

        // byteLength < Base.Length — try a shorter form: drop PublicKeyToken (still
        // resolvable for mscorlib in the game's loader, which falls back to bare type name).
        const string Shorter = "System.Object, mscorlib, Version=4.0.0.0, Culture=neutral";
        if (byteLength == Shorter.Length) return Shorter;
        if (byteLength >= Shorter.Length)
        {
            int extra = byteLength - Shorter.Length;
            return Shorter + new string(' ', extra);
        }

        // Even shorter forms — drop Culture, then Version. Last resort is bare "System.Object".
        const string NoVersion = "System.Object, mscorlib";
        if (byteLength == NoVersion.Length) return NoVersion;
        if (byteLength >= NoVersion.Length)
            return NoVersion + new string(' ', byteLength - NoVersion.Length);

        const string BareName = "System.Object";
        if (byteLength >= BareName.Length)
            return BareName + new string(' ', byteLength - BareName.Length);

        // Shorter than "System.Object" — return a same-length filler. Will likely fail
        // type resolution but at least won't be a removed-mod AQN. Caller logs a warning.
        return new string('X', byteLength);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
