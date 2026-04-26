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

    private static readonly ulong H_MOD_TYPES = ChunkHeaderULong("ModTypes");
    private static readonly ulong H_SAVE_INFO  = ChunkHeaderULong("SaveInfo");
    private static readonly ulong H_CONFIGS    = ChunkHeaderULong("GlobConf");
    private static readonly ulong H_RESOLVER   = ChunkHeaderULong("Resolver");
    private static readonly ulong H_SAVE_END   = ChunkHeaderULong("SaveStop");

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

        // CONFIGS
        progress?.Report("  Reading CONFIGS chunk…");
        ulong h3 = (ulong)_miReadULong.Invoke(reader, null)!;
        if (h3 != H_CONFIGS) throw new InvalidDataException($"Expected CONFIGS header, got 0x{h3:X16}.");
        DeserializeConfigs(reader, progress);
        // Note: FinalizeLoading for configs is called internally by deserializeConfigsWithUnknownTypeHandling.
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
