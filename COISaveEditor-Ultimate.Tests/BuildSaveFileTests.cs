using Xunit;

namespace COISaveEditorUltimate.Tests;

using COISaveEditorUltimate.DeepEdit;
using COISaveEditorUltimate.Models;
using COISaveEditorUltimate.Parsing;

public class BuildSaveFileTests
{
    [Fact]
    public void WhenBuiltThenHeaderStartsWithFileMagic()
    {
        byte[] magic = SaveFileParser.GetFileMagic();
        ParsedSave fakeSave = CreateMinimalParsedSave();
        byte[] payload = new byte[] { 0x01, 0x02, 0x03 };

        byte[] result = DeepEditEngine.BuildSaveFile(fakeSave, payload);

        Assert.True(result.Length >= 40);
        Assert.Equal(magic, result[..8]);
    }

    [Fact]
    public void WhenBuiltThenSaveVersionIsAtOffset8()
    {
        ParsedSave fakeSave = CreateMinimalParsedSave(saveVersion: 42);
        byte[] payload = new byte[] { 0xAA };

        byte[] result = DeepEditEngine.BuildSaveFile(fakeSave, payload);

        uint version = BitConverter.ToUInt32(result, 8);
        Assert.Equal(42u, version);
    }

    [Fact]
    public void WhenBuiltThenDecompressedCrcMatchesPayload()
    {
        byte[] payload = "test payload data"u8.ToArray();
        ParsedSave fakeSave = CreateMinimalParsedSave();

        byte[] result = DeepEditEngine.BuildSaveFile(fakeSave, payload);

        // Decompressed CRC is at offset 32 (8 magic + 4 ver + 4 comp + 8 compLen + 4 compCrc + 8 decompLen = 36... let me compute)
        // Header: magic(8) + version(4) + compression(4) + compressedLen(8) + compressedCrc(4) + decompressedLen(8) + decompressedCrc(4) = 40
        uint storedDecompCrc = BitConverter.ToUInt32(result, 36);
        uint expectedCrc = Crc32.Compute(payload);
        Assert.Equal(expectedCrc, storedDecompCrc);
    }

    [Fact]
    public void WhenBuiltThenTotalLengthIs40PlusCompressed()
    {
        byte[] payload = new byte[] { 1, 2, 3, 4, 5 };
        ParsedSave fakeSave = CreateMinimalParsedSave();

        byte[] result = DeepEditEngine.BuildSaveFile(fakeSave, payload);

        byte[] compressed = DeepEditEngine.GzipCompress(payload);
        Assert.Equal(40 + compressed.Length, result.Length);
    }

    private static ParsedSave CreateMinimalParsedSave(int saveVersion = 1, int compressionType = 1)
    {
        return new ParsedSave
        {
            SaveVersion = saveVersion,
            CompressionType = compressionType,
        };
    }
}
