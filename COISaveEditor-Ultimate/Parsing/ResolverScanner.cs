namespace COISaveEditorUltimate.Parsing;

/// <summary>
/// Scans the raw decompressed save bytes for assembly-qualified type names that
/// were written into the RESOLVER chunk.  Because the game uses Type.GetType()
/// with assembly-qualified names (e.g. "Mafi.Core.Buildings.Foo, Mafi.Core,
/// Version=...") and writes them as BinaryWriter raw strings (7-bit-encoded
/// byte-length prefix + UTF-8), we can find them by searching the RESOLVER bytes
/// for patterns that match the AQN format.
///
/// This is a heuristic scan — it cannot read the RESOLVER at the semantic level
/// (that requires having all mod DLLs loaded and a working DependencyResolver)
/// but it reliably extracts the list of mod-specific types stored in the save.
/// </summary>
public static class ResolverScanner
{
    private static readonly byte[] ResolverHeader = BuildHeader("Resolver");
    private static readonly byte[] VersionMarker  = ", Version="u8.ToArray();

    private static byte[] BuildHeader(string s)
    {
        var reversed = s.Reverse().ToArray();
        return System.Text.Encoding.ASCII.GetBytes(reversed);
    }

    /// <summary>
    /// Returns all assembly-qualified type name strings found in the RESOLVER chunk
    /// of the decompressed save data, grouped by assembly short name.
    /// </summary>
    public static List<ResolverType> ScanTypes(byte[] decompressedData)
    {
        int resolverOffset = FindResolverChunkStart(decompressedData);
        if (resolverOffset < 0)
            return new List<ResolverType>();

        return ExtractTypeNames(decompressedData, resolverOffset);
    }

    // ── Implementation ────────────────────────────────────────────────────

    private static int FindResolverChunkStart(byte[] data)
    {
        // Search for the 8-byte RESOLVER chunk header using Span search.
        var span = data.AsSpan();
        var pattern = ResolverHeader.AsSpan();
        int idx = span.IndexOf(pattern);
        return idx >= 0 ? idx + 8 : -1;
    }

    private static bool MatchesAt(byte[] data, int offset, byte[] pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
            if (data[offset + i] != pattern[i]) return false;
        return true;
    }

    /// <summary>
    /// Scans the bytes starting at <paramref name="start"/> looking for raw
    /// BinaryWriter-encoded strings that contain ", Version=" (AQN marker).
    /// Uses optimised first-byte check to avoid calling MatchesAt on every byte.
    /// </summary>
    private static List<ResolverType> ExtractTypeNames(byte[] data, int start)
    {
        var types   = new List<ResolverType>();
        var seen    = new HashSet<string>(StringComparer.Ordinal);
        var vMarker = VersionMarker;
        byte firstByte = vMarker[0];

        // Lazy scan: find ", Version=" occurrences and work outwards to
        // recover the full 7-bit-prefixed string.
        int searchEnd = data.Length - vMarker.Length;
        for (int i = start; i < searchEnd; i++)
        {
            // Fast skip: check first byte before full pattern match
            if (data[i] != firstByte) continue;
            if (!MatchesAt(data, i, vMarker)) continue;

            // Try to recover the full AQN string starting from some point
            // before i by decoding a 7-bit-encoded-length + UTF-8 sequence.
            string? aqn = TryDecodeStringEndingAt(data, i + vMarker.Length, start);
            if (aqn is null) continue;
            if (!seen.Add(aqn)) continue;

            types.Add(ParseAqn(aqn));
        }

        return types;
    }

    /// <summary>
    /// Works backwards from <paramref name="contentEnd"/> to find a 7-bit-encoded
    /// length prefix and a valid UTF-8 string that includes the content up to
    /// contentEnd.
    /// </summary>
    private static string? TryDecodeStringEndingAt(byte[] data, int contentEnd, int searchStart)
    {
        // Try candidate lengths from 20 up to 512 characters.
        for (int byteLen = 20; byteLen <= Math.Min(512, contentEnd - searchStart); byteLen++)
        {
            int strStart = contentEnd - byteLen;
            if (strStart < searchStart) break;

            // The 7-bit-encoded length prefix immediately precedes strStart.
            // Determine how many bytes the prefix occupies and whether they
            // encode exactly byteLen.
            int prefixLen = TryReadPrefixBefore(data, strStart, byteLen);
            if (prefixLen <= 0) continue;

            try
            {
                string s = System.Text.Encoding.UTF8.GetString(data, strStart, byteLen);
                // Quick sanity: must contain ", Version=" and look like "A.B.C, D, Version="
                if (s.Contains(", Version=") && s.Contains('.'))
                    return s;
            }
            catch { /* invalid UTF-8 sequence */ }
        }
        return null;
    }

    private static int TryReadPrefixBefore(byte[] data, int strStart, int expectedLen)
    {
        // A 7-bit-encoded int for values < 128 is 1 byte.  For 128–16383 it's 2 bytes.
        // Try each possible prefix length.
        for (int prefixLen = 1; prefixLen <= 3; prefixLen++)
        {
            int prefixStart = strStart - prefixLen;
            if (prefixStart < 0) break;

            int decoded = Decode7BitInt(data, prefixStart, out int actualLen);
            if (actualLen == prefixLen && decoded == expectedLen)
                return prefixLen;
        }
        return -1;
    }

    private static int Decode7BitInt(byte[] data, int pos, out int bytesRead)
    {
        int result = 0;
        int shift  = 0;
        bytesRead  = 0;
        while (pos < data.Length && bytesRead < 4)
        {
            byte b = data[pos++];
            bytesRead++;
            result |= (b & 0x7F) << shift;
            shift  += 7;
            if ((b & 0x80) == 0) return result;
        }
        return -1; // malformed
    }

    private static ResolverType ParseAqn(string aqn)
    {
        // Format: "Namespace.ClassName, AssemblyName, Version=..., Culture=..., PublicKeyToken=..."
        var parts     = aqn.Split(',');
        string fqName = parts[0].Trim();
        string asmName = parts.Length > 1 ? parts[1].Trim() : "?";

        int lastDot = fqName.LastIndexOf('.');
        string ns        = lastDot >= 0 ? fqName[..lastDot] : string.Empty;
        string className = lastDot >= 0 ? fqName[(lastDot + 1)..] : fqName;

        return new ResolverType
        {
            FullTypeName   = fqName,
            ClassName      = className,
            Namespace      = ns,
            AssemblyName   = asmName,
            AssemblyQualifiedName = aqn,
        };
    }
}

public sealed class ResolverType
{
    public string FullTypeName { get; init; }          = string.Empty;
    public string ClassName    { get; init; }          = string.Empty;
    public string Namespace    { get; init; }          = string.Empty;
    public string AssemblyName { get; init; }          = string.Empty;
    public string AssemblyQualifiedName { get; init; } = string.Empty;
}
