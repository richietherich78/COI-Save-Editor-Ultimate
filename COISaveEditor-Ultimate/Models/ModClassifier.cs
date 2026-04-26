namespace COISaveEditorUltimate.Models;

public enum ModCategory
{
    /// <summary>Base game module (COI-* prefix). Must stay.</summary>
    BuiltIn,

    /// <summary>Registers GlobalDependency+GenerateSerializer services that are ALWAYS
    /// written to the RESOLVER regardless of what you've placed. Cannot meaningfully
    /// be removed from a save made while this mod was installed using the sentinel
    /// approach — the RESOLVER still holds its type data and the game will throw
    /// CorruptedSaveException when loading without its DLLs.</summary>
    AlwaysUnsafe,

    /// <summary>Has serializable entity types. Only dangerous if those entities were
    /// placed on the map. Safe to remove if you've never placed any of them.</summary>
    ConditionalEntity,

    /// <summary>Only writes IConfig data to the CONFIGS chunk. The game loader skips
    /// unknown config types gracefully. Safe to remove via the sentinel approach.</summary>
    ConfigOnly,

    /// <summary>No serialized data at all. Completely safe to remove.</summary>
    NoData,

    Unknown,
}

public sealed class ModClassification
{
    public ModCategory Category { get; init; }
    public string BadgeLabel { get; init; } = string.Empty;
    /// <summary>Colour as a WPF hex colour string, e.g. "#f85149".</summary>
    public string Colour { get; init; } = string.Empty;
    public string Tooltip { get; init; } = string.Empty;
    /// <summary>When set, lists the specific entity types the user must check for.</summary>
    public string? EntityDetail { get; init; }
}

/// <summary>
/// Classifies each mod ID by its serialization footprint based on source-code
/// analysis of the mod assemblies.
/// </summary>
public static class ModClassifier
{
    // ── Always-unsafe ──────────────────────────────────────────────────────
    // [GlobalDependency] + [GenerateSerializer] on non-entity service classes.
    // These are persisted in the RESOLVER for EVERY save made while the mod
    // was installed, even if you placed zero custom buildings.

    private static readonly Dictionary<string, string> AlwaysUnsafe = new()
    {
        ["COIExtended-Core"] =
            "Registers WorldMapStateManager (world-map location cache) and " +
            "SatellitesManager (satellite deployment state) as serializable " +
            "GlobalDependency services. Both are written to the RESOLVER on every " +
            "save regardless of what buildings you have placed. The game resolves " +
            "them by assembly-qualified type name; if the DLLs are absent it throws " +
            "CorruptedSaveException immediately on load.\n\n" +
            "The Deep Edit tab can strip these objects from the RESOLVER stream, " +
            "which is the only way to remove this mod from an existing save.",
    };

    // ── Conditional entities ───────────────────────────────────────────────
    // [GenerateSerializer] on entity/command classes but NO always-on services.
    // Only present in the RESOLVER if instances were placed on the map.

    private static readonly Dictionary<string, (string entities, string tip)> Entities = new()
    {
        ["COIExtended-Automation"] = (
            "SmartZipper entity + 4 command types (belt-junction routing controller " +
            "— appears in the Logistics build menu as a splitter/merger with priority " +
            "controls).",
            "Safe to remove if you have NEVER placed a SmartZipper on any belt junction."
        ),
    };

    // ── Config only ────────────────────────────────────────────────────────
    // IConfig implementations with [GenerateSerializer], no map entities.
    // The game CONFIGS loader gracefully skips configs whose types are unknown.

    private static readonly Dictionary<string, string> ConfigOnly = new()
    {
        ["COIExtended-Cheats"]      = "Stores CheatsModConfig settings only. StorageGodMode has [GlobalDependency] but no [GenerateSerializer] — it is NOT in the save.",
        ["COIExtended-Difficulty"]  = "Stores DifficultyConfig settings only.",
        ["COIExtended-Tweaks"]      = "Stores TweaksModConfig settings only.",
        ["COIExtended-StoragePlus"] = "Stores StoragePlusConfig settings only. No entity types — no Plus-storage buildings are ever placed in the RESOLVER.",
    };

    // ── No data ────────────────────────────────────────────────────────────

    private static readonly HashSet<string> NoData = new()
    {
        "COIExtended-ItemSink",
        "COIExtended-Sanitizer",
    };

    // ──────────────────────────────────────────────────────────────────────

    public static ModClassification Classify(string modId)
    {
        if (modId.StartsWith("COI-", StringComparison.OrdinalIgnoreCase) ||
            modId.StartsWith("Mafi-", StringComparison.OrdinalIgnoreCase))
        {
            return new ModClassification
            {
                Category   = ModCategory.BuiltIn,
                BadgeLabel = "Built-in",
                Colour     = "#58a6ff",
                Tooltip    = "Core game module — do not remove.",
            };
        }

        if (AlwaysUnsafe.TryGetValue(modId, out var unsafeDesc))
        {
            return new ModClassification
            {
                Category   = ModCategory.AlwaysUnsafe,
                BadgeLabel = "Cannot Remove",
                Colour     = "#f85149",
                Tooltip    = unsafeDesc,
            };
        }

        if (Entities.TryGetValue(modId, out var entityInfo))
        {
            return new ModClassification
            {
                Category     = ModCategory.ConditionalEntity,
                BadgeLabel   = "Has Entities",
                Colour       = "#d29922",
                Tooltip      = entityInfo.tip,
                EntityDetail = entityInfo.entities,
            };
        }

        if (ConfigOnly.TryGetValue(modId, out var configDesc))
        {
            return new ModClassification
            {
                Category   = ModCategory.ConfigOnly,
                BadgeLabel = "Safe to Remove",
                Colour     = "#3fb950",
                Tooltip    = configDesc,
            };
        }

        if (NoData.Contains(modId))
        {
            return new ModClassification
            {
                Category   = ModCategory.NoData,
                BadgeLabel = "Safe to Remove",
                Colour     = "#3fb950",
                Tooltip    = "No serialized data — completely safe to remove.",
            };
        }

        return new ModClassification
        {
            Category   = ModCategory.Unknown,
            BadgeLabel = "Unknown",
            Colour     = "#8b949e",
            Tooltip    = "Unknown mod — inspect the RESOLVER Types tab to see if it adds any serialized classes.",
        };
    }
}
