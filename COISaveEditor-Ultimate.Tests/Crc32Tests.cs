using Xunit;

namespace COISaveEditorUltimate.Tests;

using COISaveEditorUltimate.Parsing;

public class Crc32Tests
{
    [Fact]
    public void WhenEmptyArrayThenReturnsCrc32OfEmpty()
    {
        uint result = Crc32.Compute(Array.Empty<byte>());

        // CRC32 of empty input is 0x00000000
        Assert.Equal(0u, result);
    }

    [Fact]
    public void WhenKnownInputThenReturnsExpectedCrc()
    {
        // CRC32 (IEEE 802.3) of ASCII "123456789" is 0xCBF43926
        byte[] data = "123456789"u8.ToArray();

        uint result = Crc32.Compute(data);

        Assert.Equal(0xCBF43926u, result);
    }

    [Fact]
    public void WhenOffsetAndCountThenComputesSubset()
    {
        byte[] data = "XX123456789YY"u8.ToArray();

        uint result = Crc32.Compute(data, 2, 9);

        Assert.Equal(0xCBF43926u, result);
    }

    [Fact]
    public void WhenStreamThenMatchesByteArrayResult()
    {
        byte[] data = "Hello, World!"u8.ToArray();
        uint expected = Crc32.Compute(data);

        using var ms = new MemoryStream(data);
        uint result = Crc32.Compute(ms);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenSameDataThenResultIsDeterministic()
    {
        byte[] data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        Assert.Equal(Crc32.Compute(data), Crc32.Compute(data));
    }
}
