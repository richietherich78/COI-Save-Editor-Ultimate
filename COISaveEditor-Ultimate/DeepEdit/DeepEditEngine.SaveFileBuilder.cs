using System.IO;
using System.IO.Compression;
using System.Reflection;
using COISaveEditorUltimate.Models;
using COISaveEditorUltimate.Parsing;

namespace COISaveEditorUltimate.DeepEdit;

public sealed partial class DeepEditEngine
{
    // ── Build final .save file from decompressed payload ─────────────────

    internal static byte[] BuildSaveFile(ParsedSave save, byte[] decompressedPayload)
    {
        byte[] compressed = GzipCompress(decompressedPayload);
        uint crcComp   = Crc32.Compute(compressed);
        uint crcDecomp = Crc32.Compute(decompressedPayload);

        byte[] magic = SaveFileParser.GetFileMagic();
        var output = new byte[40 + compressed.Length];
        int pos = 0;

        magic.CopyTo(output.AsSpan(pos, 8));                                                               pos += 8;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), (uint)save.SaveVersion);     pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), (uint)save.CompressionType); pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(pos), (ulong)compressed.LongLength); pos += 8;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), crcComp);                    pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(pos), (ulong)decompressedPayload.LongLength);  pos += 8;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), crcDecomp);                  pos += 4;
        compressed.CopyTo(output.AsSpan(pos));

        return output;
    }

    /// <summary>
    /// Streams the save file directly to disk, avoiding holding both the
    /// compressed and final output byte arrays in memory simultaneously.
    /// </summary>
    internal static void BuildSaveFileToFile(ParsedSave save, byte[] decompressedPayload, string filePath)
    {
        uint crcDecomp = Crc32.Compute(decompressedPayload);

        string tempCompressed = filePath + ".tmp.gz";
        try
        {
            using (var fs = new FileStream(tempCompressed, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            using (var gzip = new GZipStream(fs, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(decompressedPayload);
            }

            long compressedLength = new FileInfo(tempCompressed).Length;

            uint crcComp;
            {
                using var fs = new FileStream(tempCompressed, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
                crcComp = Parsing.Crc32.Compute(fs);
            }

            byte[] magic = SaveFileParser.GetFileMagic();
            var header = new byte[40];
            int pos = 0;
            magic.CopyTo(header.AsSpan(pos, 8));                                                                                    pos += 8;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(pos), (uint)save.SaveVersion);             pos += 4;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(pos), (uint)save.CompressionType);         pos += 4;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(pos), (ulong)compressedLength);            pos += 8;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(pos), crcComp);                            pos += 4;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(pos), (ulong)decompressedPayload.LongLength); pos += 8;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(pos), crcDecomp);

            using (var outFs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                outFs.Write(header);
                using var compFs = new FileStream(tempCompressed, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
                compFs.CopyTo(outFs, 81920);
            }
        }
        finally
        {
            try { File.Delete(tempCompressed); } catch { }
        }
    }

    internal static byte[] GzipCompress(byte[] data)
    {
        using var ms   = new MemoryStream();
        using var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true);
        gzip.Write(data);
        gzip.Flush();
        gzip.Close();
        return ms.ToArray();
    }

    /// <summary>
    /// Scans the serialized payload for "System.Private.CoreLib" — a .NET 8
    /// assembly name the game's Mono runtime cannot resolve.  Any hit means
    /// MonoCompatBinaryWriter failed to patch that type reference.
    /// </summary>
    internal static int ValidateNoPrivateCoreLib(byte[] payload, IProgress<string>? progress)
    {
        ReadOnlySpan<byte> needle = "System.Private.CoreLib"u8;
        int hits = 0;
        int searchFrom = 0;
        while (searchFrom < payload.Length - needle.Length)
        {
            int idx = payload.AsSpan(searchFrom).IndexOf(needle);
            if (idx < 0) break;
            int absIdx = searchFrom + idx;
            hits++;
            progress?.Report($"  [WARN] System.Private.CoreLib found at payload offset 0x{absIdx:X} — Mono AQN patch missed this reference.");
            searchFrom = absIdx + needle.Length;
        }
        if (hits == 0)
            progress?.Report("  [OK] No System.Private.CoreLib references in output.");
        else
            progress?.Report($"  [WARN] {hits} System.Private.CoreLib reference(s) found — save may fail to load on game's Mono runtime.");
        return hits;
    }

    /// <summary>
    /// If the loaded DLLs report a newer CURRENT_SAVE_VERSION than the original save,
    /// upgrades save.SaveVersion so the output header tells the game to use the newer
    /// deserialization paths (e.g. [NewInSaveVersion(289)] fields in TrainLinesManager).
    /// Without this, a save written by 0.8.3 DLLs but stamped with the old version
    /// header causes byte-stream misalignment and CorruptedSaveException on game load.
    /// </summary>
    internal static void UpgradeSaveVersionIfNeeded(ParsedSave save, IProgress<string>? progress)
    {
        try
        {
            var tSaveVersion = AssemblyLoader.FindType("Mafi.SaveVersion");
            if (tSaveVersion is null)
            {
                progress?.Report("  [SaveVersion] Mafi.SaveVersion type not found — header version unchanged.");
                return;
            }

            var fi = tSaveVersion.GetField("CURRENT_SAVE_VERSION",
                BindingFlags.Public | BindingFlags.Static);
            if (fi is null)
            {
                progress?.Report("  [SaveVersion] CURRENT_SAVE_VERSION field not found — header version unchanged.");
                return;
            }

            int dllVersion = (int)fi.GetValue(null)!;
            if (dllVersion > save.SaveVersion)
            {
                progress?.Report($"  [SaveVersion] Upgrading output header version: {save.SaveVersion} → {dllVersion} (DLL CURRENT_SAVE_VERSION).");
                save.SaveVersion = dllVersion;
            }
            else
            {
                progress?.Report($"  [SaveVersion] Output header version {save.SaveVersion} already matches DLL version {dllVersion} — no change.");
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"  [SaveVersion] Could not read DLL save version: {ex.Message} — header version unchanged.");
        }
    }

    private static DeepEditResult Fail(string msg) =>
        new DeepEditResult { Success = false, Error = msg };

    private static DeepEditResult FailWithLog(string msg, System.Text.StringBuilder detailLog) =>
        new DeepEditResult { Success = false, Error = msg, DetailedLog = detailLog.ToString() };
}
