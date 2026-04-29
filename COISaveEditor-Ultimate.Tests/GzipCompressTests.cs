using Xunit;

namespace COISaveEditorUltimate.Tests;

using System.IO.Compression;
using COISaveEditorUltimate.DeepEdit;

public class GzipCompressTests
{
    [Fact]
    public void WhenEmptyInputThenReturnsValidGzip()
    {
        byte[] result = DeepEditEngine.GzipCompress(Array.Empty<byte>());

        Assert.NotNull(result);
        Assert.True(result.Length > 0);

        // Verify it decompresses back to empty
        byte[] decompressed = Decompress(result);
        Assert.Empty(decompressed);
    }

    [Fact]
    public void WhenRoundTrippedThenDataIsPreserved()
    {
        byte[] original = "The quick brown fox jumps over the lazy dog"u8.ToArray();

        byte[] compressed = DeepEditEngine.GzipCompress(original);
        byte[] decompressed = Decompress(compressed);

        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void WhenLargeInputThenCompressesSmaller()
    {
        // Highly compressible data
        byte[] original = new byte[100_000];
        Array.Fill<byte>(original, 0x42);

        byte[] compressed = DeepEditEngine.GzipCompress(original);

        Assert.True(compressed.Length < original.Length);
    }

    private static byte[] Decompress(byte[] gzipData)
    {
        using var input = new MemoryStream(gzipData);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
