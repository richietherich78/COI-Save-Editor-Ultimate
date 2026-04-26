using System.IO;
using System.Buffers.Binary;
using System.IO.Compression;
using COISaveEditorUltimate.Models;
using COISaveEditorUltimate.Parsing;

namespace COISaveEditorUltimate.Editing;

/// <summary>
/// Exports a modified save file using the "sentinel" approach:
///
///   • Mods that are being "removed" have their IDs replaced with
///     __safe_to_delete_0__, __safe_to_delete_1__, etc. in the MOD_TYPES chunk.
///   • The RESOLVER, CONFIGS, and SAVE_INFO chunks are kept byte-for-byte identical.
///   • The outer gzip wrapper and header CRC32s are recomputed.
///
/// Why this works:
///   – The game checks MOD_TYPES to decide which mod DLLs it expects.  Sentinel
///     names don't match any registered mod, so those entries are effectively ignored.
///   – Config types from removed mods appear in the CONFIGS chunk.  The game's config
///     loader uses BlobReader.TryReadType(); if the type is unknown it backtracks and
///     skips that config gracefully.
///   – The RESOLVER chunk is unchanged.  If a removed mod had no serialised objects
///     in the RESOLVER this is safe.  Mods classified as AlwaysUnsafe or
///     ConditionalEntity (where entities were placed) CANNOT be safely removed this
///     way — the app warns the user in both cases.
///
/// The MOD_TYPES chunk is the FIRST chunk in the decompressed stream.  Because the
/// game's BlobReader maintains a string reference table across all chunks, replacing
/// a mod ID IN-PLACE (same string-table slot → same index) preserves all downstream
/// back-references unchanged.  Sentinels use the same slot as the original ID.
/// </summary>
public static class SaveExporter
{
    private static readonly ulong H_MOD_TYPES = SaveFileParser.GetChunkHeader("ModTypes");

    /// <summary>
    /// Returns the bytes of a new .save file with the specified mods replaced by
    /// sentinel IDs. Mods whose IDs are in <paramref name="modsToKeep"/> are written
    /// unchanged; any other mod gets a sentinel.
    /// </summary>
    public static byte[] Export(ParsedSave save, ISet<string> modsToKeep)
    {
        // ── 1. Rebuild the MOD_TYPES chunk bytes ──────────────────────────
        var w = new BlobBinaryWriter();
        w.WriteUIntVariable((uint)save.Mods.Count);

        int sentinelIdx = 0;
        foreach (var mod in save.Mods)
        {
            string id = modsToKeep.Contains(mod.Id)
                            ? mod.Id
                            : $"__safe_to_delete_{sentinelIdx++}__";
            w.WriteString(id);
            w.WriteUInt64LE(mod.VersionRaw);
        }

        byte[] newModTypesData = w.ToArray();

        // ── 2. Build the new decompressed payload ─────────────────────────
        //
        // Layout: [8-byte chunk header][new MOD_TYPES data][rest of original stream]
        //
        // "rest" = everything after the original MOD_TYPES data, starting from
        // ModTypesDataEnd.  This preserves SAVE_INFO, CONFIGS, RESOLVER verbatim.

        int    restStart  = save.ModTypesDataEnd;
        int    restLength = save.DecompressedData.Length - restStart;

        var payload = new byte[8 + newModTypesData.Length + restLength];
        int pos = 0;

        // Chunk header (ulong LE)
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(pos, 8), H_MOD_TYPES);
        pos += 8;

        // New MOD_TYPES data
        newModTypesData.CopyTo(payload.AsSpan(pos));
        pos += newModTypesData.Length;

        // Remaining original chunks
        save.DecompressedData.AsSpan(restStart, restLength).CopyTo(payload.AsSpan(pos));

        // ── 3. Gzip-compress the payload ──────────────────────────────────
        byte[] compressed = GzipCompress(payload);

        // ── 4. Compute CRC32s ─────────────────────────────────────────────
        uint crcComp   = Crc32.Compute(compressed);
        uint crcDecomp = Crc32.Compute(payload);

        // ── 5. Write the new outer header + compressed payload ────────────
        byte[] magic = SaveFileParser.GetFileMagic();

        var output = new byte[40 + compressed.Length];
        pos = 0;

        magic.CopyTo(output.AsSpan(pos, 8));                                     pos += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), (uint)save.SaveVersion);      pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), (uint)save.CompressionType);  pos += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(pos), (ulong)compressed.LongLength);pos += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), crcComp);                     pos += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(pos), (ulong)payload.LongLength);   pos += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), crcDecomp);                   pos += 4;

        compressed.CopyTo(output.AsSpan(pos));

        return output;
    }

    private static byte[] GzipCompress(byte[] data)
    {
        using var ms   = new MemoryStream(data.Length / 2); // pre-size estimate
        using var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true);
        gzip.Write(data);
        gzip.Flush();
        gzip.Close();
        return ms.ToArray();
    }
}
