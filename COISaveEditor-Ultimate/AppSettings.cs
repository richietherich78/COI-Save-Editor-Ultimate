using System.IO;
using System.Text.Json;

namespace COISaveEditorUltimate;

/// <summary>
/// Persists user settings (game path, mod paths) to a JSON file next to the EXE.
/// </summary>
public sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        AppContext.BaseDirectory, "COISaveEditor.settings.json");

    // ── Properties ────────────────────────────────────────────────────────

    /// <summary>
    /// Full path to the game's Managed/ folder, e.g.
    ///   C:\Program Files (x86)\Steam\steamapps\common\Captain of Industry\Captain of Industry_Data\Managed
    /// That folder contains Mafi.dll, Mafi.Core.dll, etc.
    /// </summary>
    public string GameManagedFolder { get; set; } = string.Empty;

    /// <summary>
    /// Paths to individual mod DLL files, or to folders that contain them.
    /// The user adds these when a save uses mods not in the game folder.
    /// </summary>
    public List<string> ModDllPaths { get; set; } = new();

    /// <summary>
    /// Folders to recursively scan for DLLs (added via "Load Folder" in the Deep Edit tab).
    /// </summary>
    public List<string> AdditionalDllFolders { get; set; } = new();

    /// <summary>
    /// Actual file paths of DLLs that were successfully loaded in the previous session.
    /// On next launch these are auto-loaded so the user doesn't have to click "Load" again.
    /// </summary>
    public List<string> LoadedDllPaths { get; set; } = new();

    /// <summary>If true, previously loaded DLLs are automatically restored on startup.</summary>
    public bool AutoLoadDlls { get; set; } = true;

    // ── Persistence ────────────────────────────────────────────────────────

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* first run or corrupt file — start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { /* non-fatal */ }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true + populates <paramref name="reason"/> if the folder looks
    /// like a valid COI Managed directory (contains Mafi.dll and Mafi.Core.dll).
    /// </summary>
    public static bool ValidateGameFolder(string path, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            reason = "Folder does not exist.";
            return false;
        }
        foreach (var required in new[] { "Mafi.dll", "Mafi.Core.dll" })
        {
            if (!File.Exists(Path.Combine(path, required)))
            {
                reason = $"Could not find {required} in that folder. Make sure you point to the Managed/ folder inside the game's _Data directory.";
                return false;
            }
        }
        return true;
    }
}
