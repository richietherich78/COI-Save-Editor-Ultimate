using Xunit;

namespace COISaveEditorUltimate.Tests;

using COISaveEditorUltimate.DeepEdit;

public class ChunkHeaderTests
{
    [Fact]
    public void WhenModTypesThenReturnsConsistentValue()
    {
        ulong result1 = DeepEditEngine.ChunkHeaderULong("ModTypes");
        ulong result2 = DeepEditEngine.ChunkHeaderULong("ModTypes");

        Assert.Equal(result1, result2);
    }

    [Fact]
    public void WhenDifferentStringsThenReturnsDifferentValues()
    {
        ulong modTypes = DeepEditEngine.ChunkHeaderULong("ModTypes");
        ulong saveInfo = DeepEditEngine.ChunkHeaderULong("SaveInfo");

        Assert.NotEqual(modTypes, saveInfo);
    }

    [Theory]
    [InlineData("ModTypes")]
    [InlineData("SaveInfo")]
    [InlineData("GlobConf")]
    [InlineData("Resolver")]
    [InlineData("SaveStop")]
    public void WhenEightCharStringThenProducesNonZeroValue(string header)
    {
        ulong result = DeepEditEngine.ChunkHeaderULong(header);

        Assert.NotEqual(0UL, result);
    }

    [Fact]
    public void WhenReversedStringThenMatchesExpectedEncoding()
    {
        // ChunkHeaderULong reverses the string, encodes as ASCII, reads as LE uint64.
        // For "ABCDEFGH": reversed = "HGFEDCBA", ASCII bytes = [0x48,0x47,0x46,0x45,0x44,0x43,0x42,0x41]
        // LE uint64 = 0x4142434445464748
        ulong result = DeepEditEngine.ChunkHeaderULong("ABCDEFGH");

        Assert.Equal(0x4142434445464748UL, result);
    }
}
