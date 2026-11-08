using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SubtitleTool.App.ViewModels;

namespace SubtitleTool.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        WindowFit.Attach(this);
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        vm.NewApiKey = KeyBox.Password;
        if (vm.SaveApiKey())
            KeyBox.Clear();
        else
            FocusErrorField(vm);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        if (vm.TrySave())
            DialogResult = true;
        else
            FocusErrorField(vm);
    }

    /// <summary>Lleva el foco (y la vista) al campo con el error de validación.</summary>
    private void FocusErrorField(SettingsViewModel vm)
    {
        Control? target = vm.ErrorField switch
        {
            SettingsField.ChunkSize => ChunkSizeBox,
            SettingsField.Timeout => TimeoutBox,
            SettingsField.MkvToolNixDirectory => MkvBox,
            SettingsField.FfmpegDirectory => FfmpegBox,
            SettingsField.ApiKey => KeyBox,
            _ => null,
        };
        if (target is null)
            return;
        target.BringIntoView();
        target.Focus();
        if (target is TextBox box)
            box.SelectAll();
    }

    private void OnClosing(object? sender, CancelEventArgs e) => ViewModel?.CancelPendingWork();
}
