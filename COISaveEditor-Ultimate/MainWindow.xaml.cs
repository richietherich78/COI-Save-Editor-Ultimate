using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using COISaveEditorUltimate.DeepEdit;
using COISaveEditorUltimate.Editing;
using COISaveEditorUltimate.Models;
using COISaveEditorUltimate.Parsing;

// Resolve WPF vs WinForms ambiguities (UseWindowsForms=true causes these)
using MessageBox         = System.Windows.MessageBox;
using MessageBoxButton   = System.Windows.MessageBoxButton;
using MessageBoxImage    = System.Windows.MessageBoxImage;
using CheckBox           = System.Windows.Controls.CheckBox;
using Brushes            = System.Windows.Media.Brushes;
using Color              = System.Windows.Media.Color;
using ColorConverter     = System.Windows.Media.ColorConverter;
using DataFormats        = System.Windows.DataFormats;
using DragDropEffects    = System.Windows.DragDropEffects;
using DragEventArgs      = System.Windows.DragEventArgs;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment  = System.Windows.VerticalAlignment;

namespace COISaveEditorUltimate;

public partial class MainWindow : Window
{
    private ParsedSave?       _currentSave;
    private HashSet<string>   _modsToKeep = new();
    private AppSettings       _settings   = AppSettings.Load();
    private CancellationTokenSource? _deepEditCts;
    private int               _loadedAssemblyCount = 0; // mirrors AssemblyLoader.TotalLoadedCount at last load

    public MainWindow()
    {
        InitializeComponent();
        RestoreSettings();
        AutoLoadPersistedDlls();
    }

    // ── File loading ──────────────────────────────────────────────────────

    private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Open COI Save File",
            Filter = "Save files (*.save)|*.save|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        LoadFile(dlg.FileName);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files?.FirstOrDefault() is { } path)
            LoadFile(path);
    }

    private async void LoadFile(string path)
    {
        try
        {
            SetStatus($"Loading {Path.GetFileName(path)}…");
            BtnExport.IsEnabled  = false;
            BtnExport2.IsEnabled = false;

            var (save, fileLen) = await Task.Run(() =>
            {
                byte[]     bytes = File.ReadAllBytes(path);
                ParsedSave parsed = SaveFileParser.Parse(bytes, path);
                return (parsed, bytes.Length);
            });

            _currentSave = save;
            _modsToKeep  = save.Mods.Select(m => m.Id).ToHashSet();

            PopulateUi(save);
            SetStatus($"Loaded {Path.GetFileName(path)}  —  {save.Mods.Count} mods  —  {FormatBytes(fileLen)}");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
            MessageBox.Show(ex.Message, "Failed to parse save file",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── UI population ─────────────────────────────────────────────────────

    private void PopulateUi(ParsedSave save)
    {
        // File info card
        TxtSaveFile.Text    = Path.GetFileName(save.FilePath ?? "unknown");
        TxtSaveVersion.Text = $"{save.SaveVersion}";
        TxtModCount.Text    = $"{save.Mods.Count} ({save.Mods.Count(m => m.Id.StartsWith("COI-"))} base + {save.Mods.Count(m => !m.Id.StartsWith("COI-"))} mods)";
        TxtFileInfo.Text    = Path.GetFileName(save.FilePath ?? "");

        // Mod list
        DropHint.Visibility      = Visibility.Collapsed;
        ModListScroll.Visibility = Visibility.Visible;
        ModListPanel.Children.Clear();

        foreach (var mod in save.Mods)
        {
            var panel = BuildModRow(mod);
            ModListPanel.Children.Add(panel);
        }

        BtnSelectAll.IsEnabled   = true;
        BtnDeselectAll.IsEnabled = true;
        BtnExport.IsEnabled      = true;
        BtnExport2.IsEnabled     = true;

        // RESOLVER tree
        PopulateResolverTree(save.ResolverTypeNames);

        RefreshWarnings();
    }

    private Border BuildModRow(SaveMod mod)
    {
        var cls = ModClassifier.Classify(mod.Id);

        // Checkbox
        var chk = new CheckBox
        {
            IsChecked  = true,
            Margin     = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag        = mod.Id,
        };
        // Disable checkbox for built-in mods
        if (cls.Category == ModCategory.BuiltIn)
        {
            chk.IsChecked = true;
            chk.IsEnabled = false;
        }
        chk.Checked   += ModCheckBox_Changed;
        chk.Unchecked += ModCheckBox_Changed;

        // Mod ID
        var txtId = new TextBlock
        {
            Text  = mod.Id,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // Badge
        var badge = new Border
        {
            Background    = HexBrush(cls.Colour + "22"),
            BorderBrush   = HexBrush(cls.Colour),
            BorderThickness = new Thickness(1),
            CornerRadius  = new CornerRadius(4),
            Padding       = new Thickness(6, 2, 6, 2),
            Margin        = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip       = cls.Tooltip,
            Child         = new TextBlock
            {
                Text       = cls.BadgeLabel,
                FontSize   = 10,
                Foreground = HexBrush(cls.Colour),
                FontWeight = FontWeights.SemiBold,
            },
        };

        // Version
        var txtVer = new TextBlock
        {
            Text       = mod.VersionDisplay,
            Foreground = HexBrush("#8b949e"),
            FontSize   = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(chk,    0);
        Grid.SetColumn(txtId,  1);
        Grid.SetColumn(badge,  2);
        Grid.SetColumn(txtVer, 3);

        row.Children.Add(chk);
        row.Children.Add(txtId);
        row.Children.Add(badge);
        row.Children.Add(txtVer);

        var border = new Border
        {
            BorderBrush     = HexBrush("#30363d"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding         = new Thickness(12, 8, 12, 8),
            Tag             = mod.Id,
            Child           = row,
        };
        border.MouseDown += (_, _) =>
        {
            if (chk.IsEnabled) chk.IsChecked = !chk.IsChecked;
        };
        return border;
    }

    private void ModCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox chk || chk.Tag is not string modId) return;
        if (chk.IsChecked == true)
            _modsToKeep.Add(modId);
        else
            _modsToKeep.Remove(modId);
        RefreshWarnings();
    }

    private void PopulateResolverTree(List<string> typeNames)
    {
        ResolverTree.Items.Clear();

        if (typeNames.Count == 0)
        {
            TxtResolverHint.Text       = "No mod-specific types detected in the RESOLVER chunk (save may be vanilla, or heuristic scan found nothing).";
            TxtResolverHint.Visibility = Visibility.Visible;
            ResolverTree.Visibility    = Visibility.Collapsed;
            return;
        }

        TxtResolverHint.Visibility = Visibility.Collapsed;
        ResolverTree.Visibility    = Visibility.Visible;

        var grouped = typeNames
            .Select(aqn =>
            {
                var parts  = aqn.Split(',');
                string asm = parts.Length > 1 ? parts[1].Trim() : "Unknown";
                string typ = parts[0].Trim();
                return (Assembly: asm, Type: typ, Aqn: aqn);
            })
            .GroupBy(x => x.Assembly)
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            var asmNode = new TreeViewItem
            {
                Header      = $"📦 {group.Key}  ({group.Count()})",
                Foreground  = HexBrush("#58a6ff"),
                FontWeight  = FontWeights.SemiBold,
                IsExpanded  = true,
            };
            foreach (var t in group.OrderBy(x => x.Type))
            {
                var typeNode = new TreeViewItem
                {
                    Header      = t.Type,
                    Foreground  = HexBrush("#c9d1d9"),
                    FontWeight  = FontWeights.Normal,
                    ToolTip     = t.Aqn,
                };
                asmNode.Items.Add(typeNode);
            }
            ResolverTree.Items.Add(asmNode);
        }
    }

    // ── Warnings refresh ─────────────────────────────────────────────────

    private void RefreshWarnings()
    {
        if (_currentSave is null) return;

        var removedMods = _currentSave.Mods
            .Where(m => !_modsToKeep.Contains(m.Id))
            .Select(m => (mod: m, cls: ModClassifier.Classify(m.Id)))
            .ToList();

        var alwaysUnsafe = removedMods.Where(x => x.cls.Category == ModCategory.AlwaysUnsafe).ToList();
        var entities     = removedMods.Where(x => x.cls.Category == ModCategory.ConditionalEntity).ToList();
        var configOnly   = removedMods.Where(x => x.cls.Category == ModCategory.ConfigOnly).ToList();

        BannerError.Visibility   = alwaysUnsafe.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BannerWarning.Visibility = entities.Count     > 0 ? Visibility.Visible : Visibility.Collapsed;
        BannerInfo.Visibility    = configOnly.Count   > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (alwaysUnsafe.Count > 0)
        {
            var ids = string.Join(", ", alwaysUnsafe.Select(x => x.mod.Id));
            TxtError.Text = $"✖ Cannot remove with Standard Export: {ids}\n\n" +
                "These mods register serialisable GlobalDependency services that are ALWAYS " +
                "written to the RESOLVER — even if you placed nothing from them. " +
                "Standard export only replaces mod IDs and cannot strip RESOLVER data.\n\n" +
                "→ Use the Deep Edit tab instead — it re-serialises all chunks and strips " +
                "configs + objects from removed mods.";
        }

        if (entities.Count > 0)
        {
            var lines = entities.Select(x =>
                $"• {x.mod.Id}: {x.cls.EntityDetail ?? x.cls.Tooltip}");
            TxtWarning.Text = "⚠ Entity mod removal — check before exporting:\n" +
                string.Join("\n", lines) + "\n\n" +
                "Safe only if you have NEVER placed any of those entities on the map. " +
                "Otherwise load in-game, demolish them, save, then come back here.";
        }

        if (configOnly.Count > 0)
        {
            var ids = string.Join(", ", configOnly.Select(x => x.mod.Id));
            TxtInfo.Text = $"ℹ Config mods being removed: {ids}\n\n" +
                "Standard export keeps config data unchanged — the game will attempt to skip " +
                "unknown configs, but this can fail for mods with complex config data.\n\n" +
                "→ For best results, use the Deep Edit tab which strips configs from removed mods entirely.";
        }
    }

    // ── Toolbar actions ───────────────────────────────────────────────────

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        SetAllCheckboxes(true);
    }

    private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
    {
        SetAllCheckboxes(false);
    }

    private void SetAllCheckboxes(bool check)
    {
        foreach (Border border in ModListPanel.Children.OfType<Border>())
        {
            var grid = border.Child as Grid;
            if (grid is null) continue;
            var chk = grid.Children.OfType<CheckBox>().FirstOrDefault();
            if (chk?.IsEnabled == true)
                chk.IsChecked = check;
        }
    }

    private async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSave is null) return;

        // Backup warning
        var warn = MessageBox.Show(
            "⚠ BACK UP YOUR SAVE FILES FIRST!\n\n" +
            "Editing save files can cause corruption or data loss.\n" +
            "Make sure you have a backup copy of your original save before continuing.\n\n" +
            "Do you want to proceed?",
            "Backup Reminder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (warn != MessageBoxResult.Yes) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title            = "Save Modified .save File",
            Filter           = "Save files (*.save)|*.save",
            FileName         = BuildExportFileName(_currentSave.FilePath),
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            SetStatus("Exporting…");
            BtnExport.IsEnabled = false;
            var save = _currentSave;
            var keep = _modsToKeep;
            var outPath = dlg.FileName;
            byte[] output = await Task.Run(() => SaveExporter.Export(save, keep));
            await Task.Run(() => File.WriteAllBytes(outPath, output));

            int removed = save.Mods.Count(m => !keep.Contains(m.Id));
            BtnExport.IsEnabled = true;
            SetStatus($"Exported to {Path.GetFileName(outPath)}  —  {removed} mod(s) sentinel-replaced");

            TxtExportStatus.Text       = $"✔ Exported to:\n{dlg.FileName}\n\n{removed} mod(s) replaced with sentinel IDs.  {_modsToKeep.Count} mod(s) kept unchanged.";
            TxtExportStatus.Visibility = Visibility.Visible;

            // Optionally reveal in Explorer
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}");
            MessageBox.Show(ex.ToString(), "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SetStatus(string msg) => TxtStatus.Text = msg;

    private static string FormatBytes(long bytes) =>
        bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:F1} MB" :
        bytes >= 1_024     ? $"{bytes / 1_024.0:F1} KB"     : $"{bytes} B";

    private static string BuildExportFileName(string? original, string suffix = "_modded")
    {
        if (string.IsNullOrEmpty(original)) return $"modified{suffix}.save";
        string dir  = Path.GetDirectoryName(original) ?? "";
        string stem = Path.GetFileNameWithoutExtension(original);
        return Path.Combine(dir, $"{stem}{suffix}.save");
    }

    private static SolidColorBrush HexBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return Brushes.Gray; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SETTINGS + DEEP EDIT
    // ═══════════════════════════════════════════════════════════════════════

    private void RestoreSettings()
    {
        RefreshDllConfigBanner();
        RefreshDeepEditReadiness();
    }

    private async void AutoLoadPersistedDlls()
    {
        if (!_settings.AutoLoadDlls || _settings.LoadedDllPaths.Count == 0) return;

        TxtAssemblyStatus.Text       = "Auto-loading DLLs from previous session…";
        TxtAssemblyStatus.Foreground = HexBrush("#d29922");

        var paths = _settings.LoadedDllPaths.ToList();
        var log   = new System.Text.StringBuilder();
        var prog  = new Progress<string>(msg =>
        {
            log.AppendLine(msg);
        });

        var result = await Task.Run(() => AssemblyLoader.LoadFromPaths(paths, prog));

        _loadedAssemblyCount = AssemblyLoader.TotalLoadedCount;
        int count = result.LoadedAssemblies.Count;

        if (count > 0)
        {
            TxtAssemblyStatus.Text       = $"✔ Auto-loaded {count} DLLs from previous session ({_loadedAssemblyCount} total).";
            TxtAssemblyStatus.Foreground = HexBrush("#3fb950");
            TxtAssemblyLog.Text          = log.ToString();
        }
        else
        {
            TxtAssemblyStatus.Text = "Auto-load: no new DLLs loaded (all may already be in AppDomain or paths changed).";
            TxtAssemblyStatus.Foreground = HexBrush("#8b949e");
        }

        RefreshDeepEditReadiness();
    }

    /// <summary>
    /// Opens the Settings window and refreshes UI state when it closes.
    /// </summary>
    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings) { Owner = this };
        win.ShowDialog();
        // Reload settings in case the user changed paths
        _settings = AppSettings.Load();
        RefreshDllConfigBanner();
        RefreshDeepEditReadiness();
    }

    /// <summary>
    /// Updates the DLL configuration banner in the Deep Edit tab.
    /// Shows green when configured, warning when not.
    /// </summary>
    private void RefreshDllConfigBanner()
    {
        bool hasGameFolder = AppSettings.ValidateGameFolder(_settings.GameManagedFolder, out _);
        int modPaths = _settings.ModDllPaths.Count;

        if (hasGameFolder)
        {
            DllConfigBanner.Background  = HexBrush("#0d1a2a");
            DllConfigBanner.BorderBrush = HexBrush("#58a6ff");
            TxtDllConfigStatus.Foreground = HexBrush("#58a6ff");
            TxtDllConfigStatus.Text = modPaths > 0
                ? $"✔ Game folder configured.  {modPaths} mod/DLC path(s) added."
                : "✔ Game folder configured.  No mod/DLC DLL paths added (add them in Settings if the save uses mods).";
        }
        else
        {
            DllConfigBanner.Background  = HexBrush("#1a1200");
            DllConfigBanner.BorderBrush = HexBrush("#d29922");
            TxtDllConfigStatus.Foreground = HexBrush("#d29922");
            TxtDllConfigStatus.Text = "⚠ Game DLLs not configured. Open Settings to set the game Managed folder and add mod/DLC DLLs.";
        }
    }

    private void RefreshDeepEditReadiness()
    {
        bool hasGameFolder = AppSettings.ValidateGameFolder(_settings.GameManagedFolder, out _);
        BtnLoadAssemblies.IsEnabled = hasGameFolder;
        BtnRunDeepEdit.IsEnabled    = hasGameFolder && AssemblyLoader.AreGameDllsLoaded && _currentSave is not null;
    }

    // ── Load assemblies ────────────────────────────────────────────────────

    private async void BtnLoadAssemblies_Click(object sender, RoutedEventArgs e)
    {
        BtnLoadAssemblies.IsEnabled  = false;
        TxtAssemblyStatus.Text       = "Loading…";
        TxtAssemblyStatus.Foreground = HexBrush("#d29922");
        AssemblyLogExpander.IsExpanded = true;
        TxtAssemblyLog.Text          = string.Empty;

        var log   = new System.Text.StringBuilder();
        var prog  = new Progress<string>(msg =>
        {
            log.AppendLine(msg);
            TxtAssemblyLog.Text = log.ToString();
        });

        var modPaths = _settings.ModDllPaths.ToList();

        var result = await Task.Run(() =>
            AssemblyLoader.Load(_settings.GameManagedFolder, modPaths, prog));

        _loadedAssemblyCount = AssemblyLoader.TotalLoadedCount;

        int newCount     = result.LoadedAssemblies.Count;
        int totalCount   = _loadedAssemblyCount;
        int skippedCount = result.SkippedAssemblies.Count;
        int failedCount  = result.FailedAssemblies.Count;

        if (failedCount > 0)
        {
            TxtAssemblyStatus.Text       = $"⚠ {totalCount} loaded total, {newCount} new this run, {failedCount} failed — see log.";
            TxtAssemblyStatus.Foreground = HexBrush("#f85149");
        }
        else if (newCount > 0)
        {
            TxtAssemblyStatus.Text       = $"✔ {totalCount} loaded total  ({newCount} new,  {skippedCount} already loaded / skipped).";
            TxtAssemblyStatus.Foreground = HexBrush("#3fb950");
        }
        else
        {
            TxtAssemblyStatus.Text       = $"✔ {totalCount} assemblies loaded — nothing new to add (all already in AppDomain).";
            TxtAssemblyStatus.Foreground = HexBrush("#3fb950");
        }

        log.AppendLine();
        log.AppendLine($"─ Summary: {newCount} new  |  {skippedCount} already loaded/skipped  |  {failedCount} failed  |  {totalCount} total in AppDomain ─");
        if (failedCount > 0)
            foreach (var f in result.FailedAssemblies)
                log.AppendLine("  FAIL: " + f);
        TxtAssemblyLog.Text = log.ToString();

        // Relabel button to make intent clear on next press
        BtnLoadAssemblies.Content   = "Reload All";
        BtnLoadAssemblies.IsEnabled = true;

        // Persist loaded DLL paths so they auto-restore next session
        PersistLoadedDlls();

        RefreshDeepEditReadiness();
    }

    private void PersistLoadedDlls()
    {
        _settings.LoadedDllPaths = AssemblyLoader.GetLoadedPaths().ToList();
        _settings.Save();
    }

    private async void BtnLoadFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description            = "Select a folder to recursively load all DLLs from",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        string folder = dlg.SelectedPath;
        BtnLoadAssemblies.IsEnabled = false;
        BtnLoadFolder.IsEnabled     = false;
        TxtAssemblyStatus.Text       = $"Scanning {folder}…";
        TxtAssemblyStatus.Foreground = HexBrush("#d29922");
        AssemblyLogExpander.IsExpanded = true;

        var log  = new System.Text.StringBuilder();
        var prog = new Progress<string>(msg =>
        {
            log.AppendLine(msg);
            TxtAssemblyLog.Text = log.ToString();
        });

        var result = await Task.Run(() => AssemblyLoader.LoadFolder(folder, prog));

        _loadedAssemblyCount = AssemblyLoader.TotalLoadedCount;
        int newCount    = result.LoadedAssemblies.Count;
        int failedCount = result.FailedAssemblies.Count;

        if (!result.Success && result.Error is not null)
        {
            TxtAssemblyStatus.Text       = $"✖ {result.Error}";
            TxtAssemblyStatus.Foreground = HexBrush("#f85149");
        }
        else if (failedCount > 0)
        {
            TxtAssemblyStatus.Text       = $"⚠ {newCount} loaded from folder, {failedCount} failed — {_loadedAssemblyCount} total.";
            TxtAssemblyStatus.Foreground = HexBrush("#f85149");
        }
        else if (newCount > 0)
        {
            TxtAssemblyStatus.Text       = $"✔ {newCount} loaded from folder — {_loadedAssemblyCount} total in AppDomain.";
            TxtAssemblyStatus.Foreground = HexBrush("#3fb950");
        }
        else
        {
            TxtAssemblyStatus.Text       = $"✔ No new DLLs found in that folder (all already loaded). {_loadedAssemblyCount} total.";
            TxtAssemblyStatus.Foreground = HexBrush("#3fb950");
        }

        log.AppendLine($"─ Folder load: {newCount} new | {result.SkippedAssemblies.Count} skipped | {failedCount} failed ─");
        TxtAssemblyLog.Text = log.ToString();

        // Persist the folder for future sessions + save loaded paths
        if (!_settings.AdditionalDllFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            _settings.AdditionalDllFolders.Add(folder);
        }
        PersistLoadedDlls();

        BtnLoadAssemblies.IsEnabled = true;
        BtnLoadFolder.IsEnabled     = true;
        RefreshDeepEditReadiness();
    }

    // ── Run Deep Edit ──────────────────────────────────────────────────────

    private async void BtnRunDeepEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSave is null) return;

        // Validate DLLs are configured
        if (!AppSettings.ValidateGameFolder(_settings.GameManagedFolder, out string reason))
        {
            MessageBox.Show(
                "Game DLLs are not configured.\n\n" + reason + "\n\n" +
                "Open Settings (⚙) to configure the game Managed folder before running Deep Edit.",
                "Configuration Required",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!AssemblyLoader.AreGameDllsLoaded)
        {
            MessageBox.Show(
                "Assemblies have not been loaded yet.\n\n" +
                "Click \"Load Game + Mod DLLs\" first, then run Deep Edit.",
                "Assemblies Not Loaded",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Backup warning
        var warn = MessageBox.Show(
            "⚠ BACK UP YOUR SAVE FILES FIRST!\n\n" +
            "Deep Edit re-serialises the entire save file. " +
            "This is a complex operation that could cause data loss if something goes wrong.\n\n" +
            "Make sure you have a backup copy of your original save before continuing.\n\n" +
            "Do you want to proceed?",
            "Backup Reminder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (warn != MessageBoxResult.Yes) return;

        var saveDlg = new Microsoft.Win32.SaveFileDialog
        {
            Title    = "Save Deep-Edited .save File",
            Filter   = "Save files (*.save)|*.save",
            FileName = BuildExportFileName(_currentSave.FilePath, "_deep"),
        };
        if (saveDlg.ShowDialog() != true) return;

        // UI: show progress
        BtnRunDeepEdit.IsEnabled      = false;
        BtnCancelDeepEdit.IsEnabled   = true;
        DeepEditStepBar.Visibility    = Visibility.Visible;
        TxtDeepEditStep.Visibility    = Visibility.Visible;
        DeepEditStepBar.Value         = 0;
        TxtDeepEditStep.Text          = "Step 0/8 — Starting…";
        DeepEditProgress.Visibility   = Visibility.Visible;
        TxtDeepEditPercent.Visibility = Visibility.Visible;
        DeepEditProgress.IsIndeterminate = true;
        DeepEditProgress.Value        = 0;
        TxtDeepEditPercent.Text       = "…";
        DeepEditLogBorder.Visibility  = Visibility.Visible;
        TxtDeepEditResult.Visibility  = Visibility.Collapsed;
        TxtDeepEditLog.Text           = string.Empty;

        _deepEditCts = new CancellationTokenSource();
        var ct  = _deepEditCts.Token;
        var log = new System.Text.StringBuilder();

        var modsToRemove = _currentSave.Mods
            .Where(m => !_modsToKeep.Contains(m.Id))
            .Select(m => m.Id)
            .ToHashSet();

        var save    = _currentSave;
        var outPath = saveDlg.FileName;

        // ── Live log file: streams output during execution so we can
        //    diagnose stalls even if the app appears frozen. ──────────
        var liveLogPath = Path.ChangeExtension(outPath, ".deep-edit.live.log");
        var liveLogWriter = new StreamWriter(liveLogPath, append: false, encoding: System.Text.Encoding.UTF8)
        { AutoFlush = true };
        liveLogWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Deep Edit started");
        liveLogWriter.WriteLine($"Save: {save.FilePath}  ({new FileInfo(save.FilePath).Length / (1024.0 * 1024.0):F1} MB)");
        liveLogWriter.WriteLine(new string('─', 60));

        // ── Throttled progress reporter ──────────────────────────────
        // Progress<T> marshals EVERY Report() to the UI thread via
        // SynchronizationContext.Post, flooding the dispatcher queue
        // and making the UI appear frozen.  Instead we accumulate
        // messages on the worker thread and drain them to the UI on a
        // 250 ms timer tick.
        var pendingMessages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var logLock = new object();
        var uiTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        uiTimer.Tick += (_, _) => DrainDeepEditMessages(pendingMessages, log, logLock);
        uiTimer.Start();

        // This IProgress<string> does NOT marshal to the UI thread —
        // it writes the live log file and enqueues for the timer.
        // UI log is capped to avoid StringBuilder overflow on large saves;
        // the live log file always receives the full output.
        const int MaxUiLogChars = 4_000_000;
        var prog = new DirectProgress(msg =>
        {
            lock (logLock)
            {
                if (log.Length < MaxUiLogChars)
                    log.AppendLine(msg);
                else if (log[log.Length - 1] != '…')
                    log.Append("\n… (log truncated — see .deep-edit.live.log for full output) …");
            }
            try { liveLogWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}"); } catch { }
            pendingMessages.Enqueue(msg);
        });

        var result = await Task.Run(() =>
        {
            var engine = new DeepEditEngine();
            var options = new DeepEditEngine.DeepEditOptions
            {
                AllowBrokenSaveOutput = ChkAllowBrokenSaveOutput.Dispatcher
                    .Invoke(() => ChkAllowBrokenSaveOutput.IsChecked == true)
            };
            return engine.Execute(save, modsToRemove, prog, outputFilePath: outPath, options: options);
        }, ct);

        uiTimer.Stop();
        liveLogWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Deep Edit finished (Success={result.Success})");
        liveLogWriter.Dispose();

        // Drain any remaining messages to the UI
        DrainDeepEditMessages(pendingMessages, log);
        TxtDeepEditLog.Text = log.ToString();
        DeepEditLogScroll.ScrollToBottom();

        DeepEditStepBar.Visibility    = Visibility.Collapsed;
        TxtDeepEditStep.Visibility    = Visibility.Collapsed;
        DeepEditProgress.Visibility   = Visibility.Collapsed;
        TxtDeepEditPercent.Visibility = Visibility.Collapsed;
        BtnCancelDeepEdit.IsEnabled   = false;
        BtnRunDeepEdit.IsEnabled      = true;

        bool hasOutput = result.OutputFilePath is not null
            || (result.Success && result.OutputBytes is not null);

        if (hasOutput)
        {
            // If OutputBytes is set (legacy path), write to disk
            if (result.OutputFilePath is null && result.OutputBytes is not null)
                File.WriteAllBytes(outPath, result.OutputBytes);

            // Write detailed log file next to the output save
            var logPath = Path.ChangeExtension(outPath, ".deep-edit.log");
            File.WriteAllText(logPath, result.DetailedLog);

            // Final update of the log textbox with full content
            TxtDeepEditLog.Text = log.ToString();
            DeepEditLogScroll.ScrollToBottom();

            TxtDeepEditResult.Text = $"\u2714 Deep Edit complete.\n" +
                $"  {result.ObjectsRemoved} object(s) stripped from the RESOLVER.\n" +
                $"  Saved to: {outPath}\n" +
                $"  Log: {logPath}";
            TxtDeepEditResult.Foreground = HexBrush("#3fb950");
            SetStatus($"Deep Edit saved \u2192 {Path.GetFileName(outPath)}  ({result.ObjectsRemoved} objects removed)");
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{outPath}\"");
        }
        else
        {
            // Write log even on failure
            if (!string.IsNullOrEmpty(result.DetailedLog))
            {
                var logPath = Path.ChangeExtension(outPath, ".deep-edit.log");
                File.WriteAllText(logPath, result.DetailedLog);
                TxtDeepEditResult.Text = $"\u2716 Deep Edit failed:\n{result.Error}\n\nDetailed log: {logPath}";
            }
            else
            {
                TxtDeepEditResult.Text = $"\u2716 Deep Edit failed:\n{result.Error}";
            }
            TxtDeepEditResult.Foreground = HexBrush("#f85149");
            SetStatus($"Deep Edit failed: {result.Error}");
        }
        TxtDeepEditResult.Visibility = Visibility.Visible;
    }

    private void BtnCancelDeepEdit_Click(object sender, RoutedEventArgs e)
    {
        _deepEditCts?.Cancel();
        BtnCancelDeepEdit.IsEnabled = false;
        SetStatus("Deep Edit cancelled.");
    }

    /// <summary>
    /// Drains the pending message queue from the worker thread and updates
    /// the Deep Edit progress bars + log textbox on the UI thread.
    /// Called from the DispatcherTimer tick (every 250 ms).
    /// </summary>
    private void DrainDeepEditMessages(
        System.Collections.Concurrent.ConcurrentQueue<string> queue,
        System.Text.StringBuilder log,
        object? logLock = null)
    {
        if (queue.IsEmpty) return;
        bool needsScroll = false;

        while (queue.TryDequeue(out var msg))
        {
            if (msg.StartsWith("[STEP:"))
            {
                var parts = msg.TrimStart('[').TrimEnd(']').Split(':', 4);
                if (parts.Length >= 4 && int.TryParse(parts[1], out int step) && int.TryParse(parts[2], out int total))
                {
                    DeepEditStepBar.Maximum = total;
                    DeepEditStepBar.Value = step;
                    TxtDeepEditStep.Text = $"Step {step}/{total} — {parts[3]}";
                    DeepEditProgress.IsIndeterminate = true;
                    DeepEditProgress.Value = 0;
                    TxtDeepEditPercent.Text = "…";
                }
                continue;
            }

            if (msg.StartsWith("[PROGRESS:"))
            {
                var end = msg.IndexOf(']');
                if (end > 10 && int.TryParse(msg.AsSpan(10, end - 10), out int pct))
                {
                    DeepEditProgress.IsIndeterminate = false;
                    DeepEditProgress.Value = pct;
                    TxtDeepEditPercent.Text = $"{pct}%";
                }
                continue;
            }

            if (!msg.StartsWith("    "))
                needsScroll = true;
        }

        if (needsScroll)
        {
            string snapshot;
            if (logLock is not null)
                lock (logLock) { snapshot = log.ToString(); }
            else
                snapshot = log.ToString();
            TxtDeepEditLog.Text = snapshot;
            DeepEditLogScroll.ScrollToBottom();
        }
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that invokes the callback directly on
    /// the calling thread (no SynchronizationContext marshal).  This avoids
    /// flooding the WPF dispatcher queue with thousands of Post() calls.
    /// </summary>
    private sealed class DirectProgress(Action<string> handler) : IProgress<string>
    {
        public void Report(string value) => handler(value);
    }
}
