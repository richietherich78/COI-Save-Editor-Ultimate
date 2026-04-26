namespace COISaveEditorUltimate.Parsing;

/// <summary>
/// Lightweight binary reader that mirrors the game's BlobReader encoding:
///   – Variable-length unsigned ints (LEB128)
///   – Strings via a reference table (first write = index + raw string; back-ref = just index)
///   – Raw primitive reads (7-bit-encoded int, raw LE ulong, etc.)
///
/// This is the same format our JS blobCodec.js used, verified against real saves.
/// </summary>
public sealed class BlobBinaryReader
{
    private readonly byte[] _data;
    private int _pos;
    // The game's BlobReader pre-populates m_readObjects with null at index 0.
    // Strings share the object reference table, so the first real string gets ID 1.
    private readonly List<string> _stringTable = new() { null! };

    public int Position => _pos;
    public int Length => _data.Length;
    public bool IsEof => _pos >= _data.Length;

    public BlobBinaryReader(byte[] data, int startPos = 0)
    {
        _data = data;
        _pos  = startPos;
    }

    // ── Variable-length uint (LEB128) ─────────────────────────────────────

    public uint ReadUIntVariable()
    {
        uint   result = 0;
        int    shift  = 0;
        byte   b;
        do
        {
            b = _data[_pos++];
            result |= (uint)(b & 0x7F) << shift;
            shift  += 7;
        }
        while ((b & 0x80) != 0);
        return result;
    }

    // ── 7-bit encoded int (BinaryWriter.Write(string) length prefix) ──────

    public int Read7BitEncodedInt()
    {
        int  result = 0;
        int  shift  = 0;
        byte b;
        do
        {
            b = _data[_pos++];
            result |= (b & 0x7F) << shift;
            shift  += 7;
        }
        while ((b & 0x80) != 0);
        return result;
    }

    // ── String with reference dedup table ─────────────────────────────────

    public string ReadString()
    {
        uint idx = ReadUIntVariable();

        if (idx < _stringTable.Count)
            return _stringTable[(int)idx];

        // New string — idx must equal exactly table.Count (next slot).
        string s = ReadRawString();
        _stringTable.Add(s);
        return s;
    }

    /// <summary>Reads a BinaryWriter-formatted string: 7-bit-encoded byte count + UTF-8.</summary>
    public string ReadRawString()
    {
        int byteLen = Read7BitEncodedInt();
        var s       = System.Text.Encoding.UTF8.GetString(_data, _pos, byteLen);
        _pos += byteLen;
        return s;
    }

    // ── Raw primitive reads ────────────────────────────────────────────────

    public ulong ReadUInt64LE()
    {
        ulong v = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                      _data.AsSpan(_pos, 8));
        _pos += 8;
        return v;
    }

    public uint ReadUInt32LE()
    {
        uint v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                     _data.AsSpan(_pos, 4));
        _pos += 4;
        return v;
    }

    public int ReadInt32LE() => (int)ReadUInt32LE();

    public byte ReadByte() => _data[_pos++];

    public ReadOnlySpan<byte> PeekBytes(int count) => _data.AsSpan(_pos, count);

    public int GetStringTableCount() => _stringTable.Count;

    /// <summary>Shares the current string table with a writer (for the round-trip export).</summary>
    public IReadOnlyList<string> GetStringTable() => _stringTable;
}
