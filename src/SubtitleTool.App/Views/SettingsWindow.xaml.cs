using System.Windows;
using SubtitleTool.App.ViewModels;

namespace SubtitleTool.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        vm.NewApiKey = KeyBox.Password;
        if (vm.SaveApiKey())
            KeyBox.Clear();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.TrySave() == true)
            DialogResult = true;
    }
}
