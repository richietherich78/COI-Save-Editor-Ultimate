using System.IO;
using System.Reflection;

namespace COISaveEditorUltimate.DeepEdit;

public sealed partial class DeepEditEngine
{
    // ── Chunk header constants ────────────────────────────────────────────

    // These chunk headers match SaveFileParser's ChunkHeader() computation.
    // We need them as ulongs for comparison with what BlobReader.ReadULongNonVariable returns.
    internal static ulong ChunkHeaderULong(string s)
    {
        var reversed = new string(s.Reverse().ToArray());
        var bytes = System.Text.Encoding.ASCII.GetBytes(reversed);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static readonly ulong H_MOD_TYPES  = ChunkHeaderULong("ModTypes");
    private static readonly ulong H_SAVE_INFO   = ChunkHeaderULong("SaveInfo");
    private static readonly ulong H_CONFIGS     = ChunkHeaderULong("GlobConf");
    // Legacy configs header written by older COI-Extended builds; functionally identical to
    // H_CONFIGS. We accept it on read and always write H_CONFIGS on output to modernize.
    private static readonly ulong H_CONFIGS_V2  = ChunkHeaderULong("GlobCfV2");
    private static readonly ulong H_RESOLVER    = ChunkHeaderULong("Resolver");
    private static readonly ulong H_SAVE_END    = ChunkHeaderULong("SaveStop");

    // ── Chunk traversal ───────────────────────────────────────────────────

    /// <summary>
    /// Reads MOD_TYPES, SAVE_INFO, and CONFIGS chunks via the game's BlobReader,
    /// leaving the stream positioned immediately before the RESOLVER chunk header.
    /// </summary>
    private void ReadHeaderChunks(object reader, IProgress<string>? progress)
    {
        // MOD_TYPES
        progress?.Report("  Reading MOD_TYPES chunk…");
        ulong h1 = (ulong)_miReadULong!.Invoke(reader, null)!;
        if (h1 != H_MOD_TYPES) throw new InvalidDataException($"Expected MOD_TYPES header, got 0x{h1:X16}. Decompressed data may be corrupt.");
        DeserializeModTypes(reader);
        InvokeFinalizeLoading(reader, progress);

        // SAVE_INFO
        progress?.Report("  Reading SAVE_INFO chunk…");
        ulong h2 = (ulong)_miReadULong.Invoke(reader, null)!;
        if (h2 != H_SAVE_INFO) throw new InvalidDataException($"Expected SAVE_INFO header, got 0x{h2:X16}.");
        DeserializeSaveInfo(reader);
        InvokeFinalizeLoading(reader, progress);

        // CONFIGS (also accept legacy GlobCfV2 written by older COI-Extended builds)
        progress?.Report("  Reading CONFIGS chunk…");
        ulong h3 = (ulong)_miReadULong.Invoke(reader, null)!;
        if (h3 == H_CONFIGS_V2)
        {
            progress?.Report("  Note: legacy GlobCfV2 configs header detected — seeking past raw config bytes and modernizing to GlobConf on output.");
            SeekPastLegacyConfigsV2(reader, progress);
        }
        else if (h3 != H_CONFIGS)
        {
            throw new InvalidDataException($"Expected CONFIGS header, got 0x{h3:X16}.");
        }
        else
        {
            DeserializeConfigs(reader, progress);
        }
        // Note: FinalizeLoading for configs is called internally by deserializeConfigsWithUnknownTypeHandling (GlobConf path).
        // For GlobCfV2, FinalizeLoading is a no-op since we didn't deserialize any objects into the reader.
    }

    private void ReadResolverChunkHeader(object reader)
    {
        ulong h = (ulong)_miReadULong!.Invoke(reader, null)!;
        if (h != H_RESOLVER)
            throw new InvalidDataException($"Expected RESOLVER header, got 0x{h:X16}");
    }

    // ── Chunk deserializers (via reflection) ──────────────────────────────

    private void DeserializeModTypes(object reader)
    {
        var tModsListHelper = AssemblyLoader.FindType("Mafi.Core.SaveGame.ModsListHelper");
        if (tModsListHelper is null) throw new InvalidOperationException("ModsListHelper not found.");
        var mi = tModsListHelper.GetMethod("DeserializeCustom",
            BindingFlags.Public | BindingFlags.Static);
        mi?.Invoke(null, new[] { reader });
    }

    private void DeserializeSaveInfo(object reader)
    {
        var tGameSaveInfo = AssemblyLoader.FindType("Mafi.Core.SaveGame.GameSaveInfo");
        if (tGameSaveInfo is null) return; // best-effort — missing means no read
        var mi = tGameSaveInfo.GetMethod("Deserialize",
            BindingFlags.Public | BindingFlags.Static);
        _capturedSaveInfo = mi?.Invoke(null, new[] { reader });
    }

    private void DeserializeConfigs(object reader, IProgress<string>? progress)
    {
        var tGameLoader = AssemblyLoader.FindType("Mafi.Core.SaveGame.GameLoader");
        if (tGameLoader is null)
        {
            progress?.Report("    GameLoader not found — skipping configs (FinalizeLoading only).");
            InvokeFinalizeLoading(reader, progress);
            return;
        }
        var mi = tGameLoader.GetMethod("deserializeConfigsWithUnknownTypeHandling",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (mi is null)
        {
            progress?.Report("    deserializeConfigsWithUnknownTypeHandling not found — using fallback.");
            InvokeFinalizeLoading(reader, progress);
            return;
        }
        try
        {
            var result = mi.Invoke(null, new[] { reader });
            progress?.Report("    Configs deserialized OK.");
            if (result != null)
            {
                var fiItems = result.GetType().GetField("m_items",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _capturedConfigsArray = fiItems?.GetValue(result) as Array;
            }
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            progress?.Report($"    Config deserialization warning: {tie.InnerException.Message} — continuing anyway.");
            try { InvokeFinalizeLoading(reader, progress); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The GlobCfV2 format (written by older COI-Extended) serializes config type names as full
    /// Assembly-Qualified Names embedded directly in the byte stream, rather than through BlobReader's
    /// shared type-reference table.  Attempting to deserialize it through the normal
    /// <c>deserializeConfigsWithUnknownTypeHandling</c> path corrupts the reader's stream position,
    /// causing the subsequent RESOLVER header read to land in the middle of data.
    /// <para/>
    /// This method locates the RESOLVER chunk header bytes directly in the decompressed payload,
    /// captures everything between the current stream position and that offset as the raw config bytes,
    /// then seeks the BlobReader's underlying stream to the RESOLVER header so processing can continue.
    /// The raw bytes are stored in <see cref="_rawConfigsBytes"/> for verbatim pass-through on output.
    /// </summary>
    private void SeekPastLegacyConfigsV2(object reader, IProgress<string>? progress)
    {
        // Locate the BlobReader's underlying InputStream (public property on BlobReader).
        var tReader = reader.GetType();
        var piInputStream = tReader.GetProperty("InputStream",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var stream = piInputStream?.GetValue(reader) as Stream;
        if (stream is null || !stream.CanSeek)
        {
            progress?.Report("  WARNING: Cannot seek BlobReader stream — GlobCfV2 config skip failed. RESOLVER read may fail.");
            return;
        }

        long configDataStart = stream.Position;

        // The RESOLVER header bytes (reversed "Resolver") as they appear in the raw stream.
        // We search for them from the current position forward to find where config data ends.
        byte[] resolverMagic = System.Text.Encoding.ASCII.GetBytes(
            new string("Resolver".Reverse().ToArray()));

        // Read the rest of the stream into a buffer to search.
        long remaining = stream.Length - stream.Position;
        if (remaining <= 0 || remaining > 200_000_000)
        {
            progress?.Report($"  WARNING: Unexpected remaining stream size ({remaining} bytes) while seeking past GlobCfV2. Skipping.");
            return;
        }
        byte[] buf = new byte[remaining];
        stream.ReadExactly(buf, 0, buf.Length);

        // Search for the 8-byte RESOLVER magic.
        int resolverOffset = -1;
        for (int i = 0; i <= buf.Length - 8; i++)
        {
            bool match = true;
            for (int j = 0; j < 8; j++)
            {
                if (buf[i + j] != resolverMagic[j]) { match = false; break; }
            }
            if (match) { resolverOffset = i; break; }
        }

        if (resolverOffset < 0)
        {
            progress?.Report("  WARNING: RESOLVER header not found while seeking past GlobCfV2. Save may be corrupt.");
            // Restore position
            stream.Seek(configDataStart, SeekOrigin.Begin);
            return;
        }

        // Capture the raw config bytes (everything from configDataStart up to the RESOLVER header).
        _rawConfigsBytes = buf[..resolverOffset];
        progress?.Report($"  GlobCfV2: captured {_rawConfigsBytes.Length} raw config byte(s); seeking to RESOLVER at offset {configDataStart + resolverOffset}.");

        // Seek the stream to the start of the RESOLVER header so ReadResolverChunkHeader reads it correctly.
        stream.Seek(configDataStart + resolverOffset, SeekOrigin.Begin);
    }

    private void InvokeFinalizeLoading(object reader, IProgress<string>? progress)
    {
        if (_miFinalizeLoading is null) return;
        var optNone = MakeOptionNone(_tDependencyResolver!);
        try
        {
            _miFinalizeLoading.Invoke(reader, new[] { optNone, null });
        }
        catch (TargetInvocationException tie)
        {
            progress?.Report($"    FinalizeLoading warning: {tie.InnerException?.Message ?? tie.Message}");
        }
        catch (Exception ex)
        {
            progress?.Report($"    FinalizeLoading warning: {ex.Message}");
        }
    }
}
