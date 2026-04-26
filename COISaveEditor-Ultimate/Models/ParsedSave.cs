namespace COISaveEditorUltimate.Models;

/// <summary>
/// A mod entry as stored in the MOD_TYPES chunk of a save file.
/// </summary>
public sealed class SaveMod
{
    public string Id { get; init; } = string.Empty;

    /// <summary>Raw 8-byte LE ulong exactly as it sits in the save stream.</summary>
    public ulong VersionRaw { get; init; }

    /// <summary>Human-readable version string, e.g. "0.8.1a".</summary>
    public string VersionDisplay { get; init; } = string.Empty;
}

/// <summary>
/// Everything we read from a .save file, plus offsets needed to rewrite it.
/// </summary>
public sealed class ParsedSave
{
    // ── Outer header (first 40 bytes of file) ──────────────────────────────

    public int SaveVersion { get; init; }
    public int CompressionType { get; init; }

    // ── Decompressed payload ───────────────────────────────────────────────

    /// <summary>Full decompressed blob (all chunks concatenated).</summary>
    public byte[] DecompressedData { get; init; } = Array.Empty<byte>();

    /// <summary>Byte offset inside DecompressedData where MOD_TYPES data begins
    /// (just AFTER the 8-byte chunk header).</summary>
    public int ModTypesDataStart { get; init; }

    /// <summary>Byte offset inside DecompressedData where MOD_TYPES data ends.</summary>
    public int ModTypesDataEnd { get; init; }

    // ── Parsed content ─────────────────────────────────────────────────────

    public List<SaveMod> Mods { get; init; } = new();

    /// <summary>Save info strings as written in the SAVE_INFO chunk (free-form).</summary>
    public Dictionary<string, string> SaveInfo { get; init; } = new();

    /// <summary>Assembly-qualified type names found in the RESOLVER chunk.</summary>
    public List<string> ResolverTypeNames { get; init; } = new();

    /// <summary>Path to the file that was opened, if any.</summary>
    public string? FilePath { get; init; }
}
