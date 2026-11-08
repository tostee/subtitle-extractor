using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using SubtitleTool.App.ViewModels;
using SubtitleTool.App.Views;

namespace SubtitleTool.App.Services;

/// <summary>Implementación WPF de los diálogos.</summary>
public sealed class DialogService : IDialogService
{
    public Window? Owner { get; set; }

    private Window? ActiveOwner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Owner;

    public string? PickFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        return dlg.ShowDialog(ActiveOwner) == true ? dlg.FolderName : null;
    }

    public IReadOnlyList<string> PickVideoFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Elige uno o varios videos (.mkv, .mp4)",
            Filter = SubtitleTool.Core.Media.VideoFormats.DialogFilter,
            Multiselect = true,
        };
        return dlg.ShowDialog(ActiveOwner) == true ? dlg.FileNames : [];
    }

    public void ShowInfo(string message, string title = "Subtítulos") => Show(message, title, MessageBoxImage.Information);
    public void ShowWarning(string message, string title = "Subtítulos") => Show(message, title, MessageBoxImage.Warning);
    public void ShowError(string message, string title = "Subtítulos") => Show(message, title, MessageBoxImage.Error);

    private void Show(string message, string title, MessageBoxImage image)
    {
        if (ActiveOwner is { } owner)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, image);
    }

    public bool Confirm(string message, string title = "Subtítulos") =>
        (ActiveOwner is { } owner
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)) == MessageBoxResult.Yes;

    public void OpenFolder(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowError("No se pudo abrir la carpeta: " + ex.Message);
        }
    }

    public void OpenUrl(string url) => OpenFile(url);

    public void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowError("No se pudo abrir el archivo: " + ex.Message);
        }
    }

    public void ShowText(string title, string text) =>
        new TextWindow { Owner = ActiveOwner, Title = title, DataContext = text }.ShowDialog();

    public bool ShowSettings(SettingsViewModel viewModel) =>
        new SettingsWindow { Owner = ActiveOwner, DataContext = viewModel }.ShowDialog() == true;

    public void ShowGlossaryEditor(GlossaryEditorViewModel viewModel) =>
        new GlossaryWindow { Owner = ActiveOwner, DataContext = viewModel }.ShowDialog();

    public void ShowReview(ReviewViewModel viewModel) =>
        new ReviewWindow(viewModel) { Owner = ActiveOwner }.ShowDialog();

    public void ShowSummary(IReadOnlyList<SummaryRow> rows) =>
        new SummaryWindow { Owner = ActiveOwner, DataContext = rows }.ShowDialog();
}
