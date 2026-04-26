using System.IO;
using System.IO.Hashing;

namespace COISaveEditorUltimate.Parsing;

/// <summary>
/// CRC32 using the .NET built-in hardware-accelerated implementation.
/// Falls back to software if SSE4.2/ARM CRC is unavailable.
/// Matches the game's CRC32 (IEEE 802.3 polynomial).
/// </summary>
public static class Crc32
{
    public static uint Compute(byte[] data)
        => Compute(data, 0, data.Length);

    public static uint Compute(byte[] data, int offset, int count)
    {
        var crc = new System.IO.Hashing.Crc32();
        crc.Append(data.AsSpan(offset, count));
        return crc.GetCurrentHashAsUInt32();
    }

    public static uint Compute(Stream stream)
    {
        var crc = new System.IO.Hashing.Crc32();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            crc.Append(buffer.AsSpan(0, read));
        return crc.GetCurrentHashAsUInt32();
    }
}
