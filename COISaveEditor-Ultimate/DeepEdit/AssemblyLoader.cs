using System.IO;
using System.Reflection;

namespace COISaveEditorUltimate.DeepEdit;

/// <summary>
/// Loads game and mod assemblies into the current AppDomain so that
/// Type.GetType(assemblyQualifiedName) — which is what BlobReader calls
/// internally — resolves all known types correctly.
///
/// The game ships its Managed/ folder with Mafi.dll, Mafi.Core.dll, Mafi.Base.dll,
/// Mafi.Unity.dll, Mafi.UnityCore.dll, plus many System.* and Unity* DLLs.
///
/// We load: required game logic DLLs + any mod DLLs the user points us at.
/// We deliberately skip Unity engine DLLs (UnityEngine.*) and System.* libs that
/// are already in the .NET 8 runtime to avoid conflicts.
/// </summary>
public static class AssemblyLoader
{
    // Game-logic DLLs we must load (order matters — dependencies first).
    private static readonly string[] RequiredGameDlls =
    {
        "Mafi.dll",
        "Mafi.Core.dll",
        "Mafi.Base.dll",
        "Mafi.UnityCore.dll",
        // Mafi.Unity.dll has hard Unity render-engine deps — skip unless needed
    };

    // Prefixes of DLLs we must NOT load (Unity render engine, conflicts with .NET 8 BCL).
    private static readonly string[] SkipPrefixes =
    {
        "UnityEngine.",
        "Unity.",
        "System.",
        "Microsoft.",
        "mscorlib",
        "netstandard",
        "Mono.",
        "Facepunch.",
    };

    public sealed class LoadResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public List<string> LoadedAssemblies { get; } = new();
        public List<string> SkippedAssemblies { get; } = new();
        public List<string> FailedAssemblies  { get; } = new();
    }

    private static readonly HashSet<string> s_alreadyLoaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> s_loadedPaths   = new(StringComparer.OrdinalIgnoreCase);

    public static bool AreGameDllsLoaded => s_alreadyLoaded.Count > 0;
    public static int  TotalLoadedCount   => s_alreadyLoaded.Count;

    /// <summary>
    /// Loads required game DLLs from <paramref name="managedFolder"/>, then
    /// loads any additional mod DLLs from <paramref name="modDllPaths"/>.
    /// </summary>
    public static LoadResult Load(string managedFolder, IEnumerable<string> modDllPaths,
                                  IProgress<string>? progress = null)
    {
        var result = new LoadResult { Success = true };

        // ── 1. Required game DLLs ─────────────────────────────────────────
        foreach (var name in RequiredGameDlls)
        {
            string path = Path.Combine(managedFolder, name);
            TryLoadOne(path, result, progress);
        }

        // ── 2. All other Mafi.* DLLs in the Managed folder ──────────────────
        foreach (var dll in Directory.GetFiles(managedFolder, "Mafi*.dll"))
        {
            TryLoadOne(dll, result, progress);
        }

        // ── 3. Mafi.* DLLs in sibling folders of Managed (DLC DLLs, etc.) ──
        // DLC DLLs like Mafi.TrainsDlc.dll may live in a subfolder next to
        // the Managed folder rather than inside it.
        try
        {
            string? parentDir = Path.GetDirectoryName(managedFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parentDir is not null)
            {
                foreach (var subDir in Directory.GetDirectories(parentDir))
                {
                    if (string.Equals(subDir, managedFolder, StringComparison.OrdinalIgnoreCase))
                        continue; // already done above
                    foreach (var dll in Directory.GetFiles(subDir, "Mafi*.dll", SearchOption.TopDirectoryOnly))
                        TryLoadOne(dll, result, progress);
                }
            }
        }
        catch { /* best-effort DLC scan — never fatal */ }

        // ── 4. Mod DLLs ───────────────────────────────────────────────────
        foreach (var entry in modDllPaths)
        {
            if (File.Exists(entry))
            {
                TryLoadOne(entry, result, progress);
            }
            else if (Directory.Exists(entry))
            {
                foreach (var dll in Directory.GetFiles(entry, "*.dll", SearchOption.TopDirectoryOnly))
                    TryLoadOne(dll, result, progress);
            }
        }

        result.Success = result.FailedAssemblies.Count == 0 || result.LoadedAssemblies.Count > 0;
        return result;
    }

    private static void TryLoadOne(string path, LoadResult result, IProgress<string>? progress)
    {
        string name = Path.GetFileName(path);
        if (ShouldSkip(name))
        {
            result.SkippedAssemblies.Add(name);
            return;
        }
        if (s_alreadyLoaded.Contains(name))
            return;

        try
        {
            Assembly.LoadFrom(path);
            s_alreadyLoaded.Add(name);
            s_loadedPaths.Add(Path.GetFullPath(path));
            result.LoadedAssemblies.Add(name);
            progress?.Report($"Loaded {name}");
        }
        catch (Exception ex)
        {
            result.FailedAssemblies.Add($"{name}: {ex.Message}");
            progress?.Report($"SKIP  {name}  ({ex.Message})");
        }
    }

    private static bool ShouldSkip(string filename)
    {
        foreach (var prefix in SkipPrefixes)
            if (filename.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Re-loads DLLs from a list of previously persisted file paths.
    /// Used on startup to restore assemblies from the last session.
    /// </summary>
    public static LoadResult LoadFromPaths(IEnumerable<string> dllPaths,
                                           IProgress<string>? progress = null)
    {
        var result = new LoadResult { Success = true };
        foreach (var path in dllPaths)
        {
            if (File.Exists(path))
                TryLoadOne(path, result, progress);
        }
        result.Success = result.FailedAssemblies.Count == 0 || result.LoadedAssemblies.Count > 0;
        return result;
    }

    /// <summary>
    /// Recursively scans <paramref name="folderPath"/> and all subdirectories
    /// for *.dll files and loads them (respecting skip prefixes).
    /// </summary>
    public static LoadResult LoadFolder(string folderPath, IProgress<string>? progress = null)
    {
        var result = new LoadResult { Success = true };
        if (!Directory.Exists(folderPath))
        {
            result.Success = false;
            result.Error = $"Folder not found: {folderPath}";
            return result;
        }

        progress?.Report($"Scanning {folderPath} (recursive)…");
        string[] dlls;
        try
        {
            dlls = Directory.GetFiles(folderPath, "*.dll", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = $"Failed to scan folder: {ex.Message}";
            return result;
        }

        progress?.Report($"Found {dlls.Length} DLL(s) — loading…");
        foreach (var dll in dlls)
            TryLoadOne(dll, result, progress);

        result.Success = result.FailedAssemblies.Count == 0 || result.LoadedAssemblies.Count > 0;
        return result;
    }

    /// <summary>
    /// Returns the full file paths of all DLLs that have been successfully loaded
    /// via this loader (for persistence across sessions).
    /// </summary>
    public static IReadOnlyCollection<string> GetLoadedPaths() => s_loadedPaths;

    /// <summary>
    /// Searches all loaded assemblies for a type by its full name (no assembly qualification).
    /// Useful for diagnostics.
    /// </summary>
    public static Type? FindType(string fullTypeName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullTypeName, throwOnError: false);
            if (t is not null) return t;
        }
        return null;
    }
}


