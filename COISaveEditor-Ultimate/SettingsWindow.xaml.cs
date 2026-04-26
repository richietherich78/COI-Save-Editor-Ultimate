using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using MessageBox       = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using Color            = System.Windows.Media.Color;
using ColorConverter   = System.Windows.Media.ColorConverter;

namespace COISaveEditorUltimate;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        RestoreFromSettings();
    }

    private void RestoreFromSettings()
    {
        TxtGameFolder.Text = _settings.GameManagedFolder;
        ModDllList.Items.Clear();
        foreach (var path in _settings.ModDllPaths)
            ModDllList.Items.Add(path);
        ValidateGameFolder();
    }

    // ── Game folder ────────────────────────────────────────────────────

    private void BtnBrowseGameFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description            = "Select the game's Managed folder (contains Mafi.dll)",
            UseDescriptionForTitle = true,
            SelectedPath           = _settings.GameManagedFolder,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        TxtGameFolder.Text = dlg.SelectedPath;
    }

    private void TxtGameFolder_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateGameFolder();
    }

    private void ValidateGameFolder()
    {
        string path = TxtGameFolder.Text.Trim();
        bool valid  = AppSettings.ValidateGameFolder(path, out string reason);

        TxtGameFolderStatus.Text       = valid ? "✔ Mafi.dll and Mafi.Core.dll found." : reason;
        TxtGameFolderStatus.Foreground = valid ? HexBrush("#3fb950") : HexBrush("#f85149");

        _settings.GameManagedFolder = valid ? path : _settings.GameManagedFolder;
        if (valid) _settings.Save();
    }

    // ── Mod DLL list ───────────────────────────────────────────────────

    private void BtnAddModDll_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title       = "Add mod DLL or select a folder (cancel to browse folders)",
            Filter      = "DLL files (*.dll)|*.dll|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true)
        {
            using var folderDlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description            = "Select a folder containing mod DLLs",
                UseDescriptionForTitle = true,
            };
            if (folderDlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                AddEntry(folderDlg.SelectedPath);
            return;
        }
        foreach (var f in dlg.FileNames)
            AddEntry(f);
    }

    private void AddEntry(string path)
    {
        if (ModDllList.Items.Contains(path)) return;
        ModDllList.Items.Add(path);
        _settings.ModDllPaths.Add(path);
        _settings.Save();
    }

    private void BtnRemoveModDll_Click(object sender, RoutedEventArgs e)
    {
        if (ModDllList.SelectedItem is not string sel) return;
        ModDllList.Items.Remove(sel);
        _settings.ModDllPaths.Remove(sel);
        _settings.Save();
    }

    // ── Close ──────────────────────────────────────────────────────────

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        // Final validate + save on close
        string path = TxtGameFolder.Text.Trim();
        if (AppSettings.ValidateGameFolder(path, out _))
        {
            _settings.GameManagedFolder = path;
            _settings.Save();
        }
        DialogResult = true;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static SolidColorBrush HexBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return System.Windows.Media.Brushes.Gray; }
    }
}
