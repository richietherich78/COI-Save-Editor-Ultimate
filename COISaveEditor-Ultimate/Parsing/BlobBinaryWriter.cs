namespace COISaveEditorUltimate.Parsing;

/// <summary>
/// Lightweight binary writer that mirrors the game's BlobWriter encoding, suitable
/// for rebuilding the MOD_TYPES chunk with sentinel replacements.
/// </summary>
public sealed class BlobBinaryWriter
{
    private readonly System.IO.MemoryStream _stream = new();
    // The game's BlobWriter pre-populates m_writtenObjects with {null → 0}.
    // Strings share the object reference table, so the first string gets ID 1.
    private readonly List<string> _stringTable = new() { null! };

    public byte[] ToArray() => _stream.ToArray();
    public long Length => _stream.Length;

    // ── Variable-length uint (LEB128) ─────────────────────────────────────

    public void WriteUIntVariable(uint value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            _stream.WriteByte(b);
        }
        while (value != 0);
    }

    // ── 7-bit encoded int (BinaryWriter.Write(string) length prefix) ──────

    public void Write7BitEncodedInt(int value)
    {
        uint uval = (uint)value;
        while (uval >= 0x80)
        {
            _stream.WriteByte((byte)(uval | 0x80));
            uval >>= 7;
        }
        _stream.WriteByte((byte)uval);
    }

    // ── String with reference dedup table ─────────────────────────────────

    public void WriteString(string s)
    {
        int idx = _stringTable.IndexOf(s);
        if (idx >= 0)
        {
            WriteUIntVariable((uint)idx);
        }
        else
        {
            WriteUIntVariable((uint)_stringTable.Count);
            _stringTable.Add(s);
            WriteRawString(s);
        }
    }

    private void WriteRawString(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        Write7BitEncodedInt(bytes.Length);
        _stream.Write(bytes);
    }

    // ── Raw primitive writes ───────────────────────────────────────────────

    public void WriteUInt64LE(ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        _stream.Write(buf);
    }

    public void WriteUInt32LE(uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        _stream.Write(buf);
    }

    public void WriteBytes(byte[] bytes) => _stream.Write(bytes);
    public void WriteBytes(byte[] bytes, int offset, int count) => _stream.Write(bytes, offset, count);
    public void WriteByte(byte b) => _stream.WriteByte(b);
}
