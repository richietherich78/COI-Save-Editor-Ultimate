using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using COISaveEditorUltimate.DeepEdit;
using COISaveEditorUltimate.Parsing;
using Application = System.Windows.Application;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBox = System.Windows.MessageBox;

[assembly: InternalsVisibleTo("COISaveEditor-Ultimate.Tests")]

namespace COISaveEditorUltimate;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        if (args.Length >= 1 && args[0] == "--headless")
        {
            // --headless <savepath> <outpath> [mod-id-to-remove ...]
            // If no mod IDs supplied, removes all mods that contain "COIExtended" in the ID.
            RunHeadless(args);
            Shutdown(0);
            return;
        }

        // Normal GUI mode.
        // Surface any unhandled exceptions with a dialog instead of silent crash.
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.ToString(), "Unhandled error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        new MainWindow().Show();
    }

    private static void RunHeadless(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: COISaveEditor-Ultimate.exe --headless <savepath> <outpath> [mod-id ...]");
            Environment.Exit(1);
            return;
        }

        string savePath = args[1];
        string outPath  = args[2];

        Console.WriteLine($"[headless] Save:   {savePath}");
        Console.WriteLine($"[headless] Output: {outPath}");

        // Load settings and DLLs.
        var settings = AppSettings.Load();
        Console.WriteLine($"[headless] Loading DLLs from {settings.GameManagedFolder} …");
        var loadResult = AssemblyLoader.Load(settings.GameManagedFolder, settings.ModDllPaths,
            new SyncProgress(m => Console.WriteLine($"  [dll] {m}")));
        if (!loadResult.Success)
        {
            Console.Error.WriteLine($"[headless] DLL load failed: {loadResult.Error}");
            Environment.Exit(2);
            return;
        }
        Console.WriteLine($"[headless] DLLs loaded: {AssemblyLoader.TotalLoadedCount} assemblies.");

        // Parse save.
        Console.WriteLine("[headless] Parsing save file…");
        byte[] bytes = File.ReadAllBytes(savePath);
        var save = SaveFileParser.Parse(bytes, savePath);
        Console.WriteLine($"[headless] Save parsed: {save.Mods.Count} mod(s) in MOD_TYPES.");

        // Determine mods to remove.
        HashSet<string> modsToRemove;
        if (args.Length >= 4)
        {
            modsToRemove = new HashSet<string>(args.Skip(3), StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            modsToRemove = save.Mods
                .Where(m => m.Id.Contains("COIExtended", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Id)
                .ToHashSet();
        }
        Console.WriteLine($"[headless] Mods to remove ({modsToRemove.Count}): {string.Join(", ", modsToRemove)}");

        // Set up live log.
        string liveLogPath = Path.ChangeExtension(outPath, ".deep-edit.live.log");
        using var liveLog  = new StreamWriter(liveLogPath, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
        liveLog.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Deep Edit started (headless)");

        var prog = new SyncProgress(msg =>
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            Console.WriteLine(line);
            liveLog.WriteLine(line);
        });

        // Run.
        Console.WriteLine("[headless] Running DeepEditEngine…");
        var engine = new DeepEditEngine();
        var result = engine.Execute(save, modsToRemove, prog, outputFilePath: outPath);

        liveLog.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Deep Edit finished (Success={result.Success})");

        // Write detailed log.
        string logPath = Path.ChangeExtension(outPath, ".deep-edit.log");
        File.WriteAllText(logPath, result.DetailedLog);

        if (result.Success)
        {
            Console.WriteLine($"[headless] SUCCESS — {result.ObjectsRemoved} object(s) removed.");
            Console.WriteLine($"[headless] Output:   {outPath}  ({new FileInfo(outPath).Length / (1024.0 * 1024.0):F1} MB)");
            Console.WriteLine($"[headless] Log:      {logPath}");
        }
        else
        {
            Console.Error.WriteLine($"[headless] FAILED: {result.Error}");
            Environment.Exit(3);
        }
    }

    private sealed class SyncProgress(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }
}
