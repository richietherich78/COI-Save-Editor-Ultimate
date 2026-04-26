using System.IO;
using System.IO.Compression;
using System.Buffers.Binary;
using COISaveEditorUltimate.Models;

namespace COISaveEditorUltimate.Parsing;

/// <summary>
/// Parses a Captain of Industry .save file.
///
/// File layout:
///   [40-byte outer header]  [gzip-compressed payload]
///
/// Outer header:
///   [0..7]   : magic = "MaFiSave" with bytes reversed  (= "evaS iFaM" in file)
///   [8..11]  : save version   (uint32 LE) — typically 286
///   [12..15] : compression    (uint32 LE) — 1 = gzip
///   [16..23] : compressed size   (uint64 LE)
///   [24..27] : CRC32 of compressed bytes   (uint32 LE)
///   [28..35] : uncompressed size (uint64 LE)
///   [36..39] : CRC32 of uncompressed bytes (uint32 LE)
///
/// Decompressed payload is a sequence of [8-byte chunk-header][chunk data] blocks:
///   MOD_TYPES → SAVE_INFO → CONFIGS → RESOLVER → SAVE_END
/// All chunks share a single BlobReader stream (string table is cumulative).
/// </summary>
public static class SaveFileParser
{
    // ── Chunk headers (ASCII string reversed, packed as LE uint64) ────────

    private static readonly ulong H_MOD_TYPES = ChunkHeader("ModTypes");
    private static readonly ulong H_SAVE_INFO  = ChunkHeader("SaveInfo");
    private static readonly ulong H_CONFIGS    = ChunkHeader("GlobConf");
    private static readonly ulong H_RESOLVER   = ChunkHeader("Resolver");
    private static readonly ulong H_SAVE_END   = ChunkHeader("SaveStop");

    // Outer file magic: "MaFiSave" reversed as bytes
    private static readonly byte[] MAGIC = "evaS iFaM"u8.ToArray()[..8];
    // Actually: reverse of "MaFiSave" = "evaS", "iFaM" ← need to compute properly
    // Let's define it correctly:
    private static readonly byte[] FILE_MAGIC = ComputeMagic("MaFiSave");

    private static byte[] ComputeMagic(string s)
        => System.Text.Encoding.ASCII.GetBytes(new string(s.Reverse().ToArray()));

    private static ulong ChunkHeader(string s)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(new string(s.Reverse().ToArray()));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>Parses a .save file from raw bytes.</summary>
    public static ParsedSave Parse(byte[] fileBytes, string? filePath = null)
    {
        if (fileBytes.Length < 40)
            throw new InvalidDataException("File is too small to be a valid save.");

        ValidateMagic(fileBytes);

        int  saveVersion    = (int)BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(8));
        int  compressionType = (int)BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(12));
        // Sizes at [16] and [28] — we don't need them for parsing since we read to end.
        uint crcCompressed   = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(24));
        uint crcUncompressed = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(36));

        // Decompress
        byte[] decompressed = Decompress(fileBytes, 40);

        // Validate CRCs
        uint actualCrcComp   = Crc32.Compute(fileBytes, 40, fileBytes.Length - 40);
        uint actualCrcDecomp = Crc32.Compute(decompressed);
        if (actualCrcComp   != crcCompressed)   throw new InvalidDataException($"CRC32 mismatch on compressed data (expected 0x{crcCompressed:X8}, got 0x{actualCrcComp:X8}).");
        if (actualCrcDecomp != crcUncompressed) throw new InvalidDataException($"CRC32 mismatch on decompressed data (expected 0x{crcUncompressed:X8}, got 0x{actualCrcDecomp:X8}).");

        // Parse chunks
        var reader   = new BlobBinaryReader(decompressed);
        var mods     = new List<SaveMod>();
        var saveInfo = new Dictionary<string, string>();
        int modTypesDataStart = 0;
        int modTypesDataEnd   = 0;

        while (!reader.IsEof)
        {
            ulong header = reader.ReadUInt64LE();

            if (header == H_MOD_TYPES)
            {
                modTypesDataStart = reader.Position;
                mods              = ReadModTypes(reader);
                modTypesDataEnd   = reader.Position;
            }
            else if (header == H_SAVE_INFO)
            {
                saveInfo = ReadSaveInfo(reader);
            }
            else if (header == H_CONFIGS || header == H_RESOLVER)
            {
                // We don't parse these — skip to end (they fill the rest of the payload).
                break;
            }
            else if (header == H_SAVE_END)
            {
                break;
            }
            else
            {
                // Unknown chunk — stop safely rather than reading garbage.
                break;
            }
        }

        // Scan RESOLVER for type names (heuristic)
        var resolverTypes = ResolverScanner.ScanTypes(decompressed);

        return new ParsedSave
        {
            SaveVersion        = saveVersion,
            CompressionType    = compressionType,
            DecompressedData   = decompressed,
            ModTypesDataStart  = modTypesDataStart,
            ModTypesDataEnd    = modTypesDataEnd,
            Mods               = mods,
            SaveInfo           = saveInfo,
            ResolverTypeNames  = resolverTypes.Select(t => t.AssemblyQualifiedName).ToList(),
            FilePath           = filePath,
        };
    }

    // ── Chunk parsers ─────────────────────────────────────────────────────

    private static List<SaveMod> ReadModTypes(BlobBinaryReader reader)
    {
        uint count = reader.ReadUIntVariable();
        var  mods  = new List<SaveMod>((int)count);

        for (uint i = 0; i < count; i++)
        {
            string id      = reader.ReadString();
            ulong  version = reader.ReadUInt64LE();
            mods.Add(new SaveMod
            {
                Id             = id,
                VersionRaw     = version,
                VersionDisplay = FormatVersion(version),
            });
        }

        return mods;
    }

    private static Dictionary<string, string> ReadSaveInfo(BlobBinaryReader reader)
    {
        // SAVE_INFO contains a GameSaveInfo object serialised via GenerateSerializer.
        // We do a best-effort read of simple key-value strings using the string table.
        // If parsing fails we just return empty.
        var result = new Dictionary<string, string>();
        try
        {
            // The object is serialised as BlobWriter object-reference format:
            // [4-byte object ID int][then delayed data: fields serialised as strings]
            // We just read whatever strings we can find and label them generically.
            int count = (int)reader.ReadUIntVariable();
            for (int i = 0; i < Math.Min(count, 20); i++)
            {
                string key = reader.ReadString();
                string val = reader.ReadString();
                if (!string.IsNullOrEmpty(key))
                    result[key] = val;
            }
        }
        catch { /* best-effort — don't crash if format doesn't match */ }
        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void ValidateMagic(byte[] data)
    {
        for (int i = 0; i < FILE_MAGIC.Length; i++)
            if (data[i] != FILE_MAGIC[i])
                throw new InvalidDataException("File does not begin with the expected COI save magic. Is this a .save file from Captain of Industry?");
    }

    private static byte[] Decompress(byte[] data, int offset)
    {
        using var compressed   = new MemoryStream(data, offset, data.Length - offset);
        using var gzip         = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: false);
        using var decompressed = new MemoryStream(data.Length * 2); // pre-size for typical compression ratio
        gzip.CopyTo(decompressed, 81920); // 80KB buffer for better throughput
        return decompressed.ToArray();
    }

    /// <summary>
    /// Formats a VersionSlim ulong into a human-readable string.
    /// VersionSlim uses StructLayout.Explicit with ushort fields at:
    ///   offset 0 (bits  0–15): Hotfix  (0 = none, 1 = 'a', 2 = 'b', …)
    ///   offset 2 (bits 16–31): Patch
    ///   offset 4 (bits 32–47): Minor
    ///   offset 6 (bits 48–63): Major
    /// </summary>
    public static string FormatVersion(ulong v)
    {
        int hotfix = (int)(v         & 0xFFFF);
        int patch  = (int)((v >> 16) & 0xFFFF);
        int minor  = (int)((v >> 32) & 0xFFFF);
        int major  = (int)((v >> 48) & 0xFFFF);

        string s = $"{major}.{minor}.{patch}";
        if (hotfix > 0 && hotfix <= 26)
            s += (char)(96 + hotfix); // 1='a', 2='b', …
        else if (hotfix > 26)
            s += $".{hotfix}";
        return s;
    }

    /// <summary>Exposes chunk-header computation for use in the exporter.</summary>
    public static ulong GetChunkHeader(string chunkName) => ChunkHeader(chunkName);
    public static byte[] GetFileMagic() => (byte[])FILE_MAGIC.Clone();
}
