using System.Windows;
using System.Windows.Input;
using SubtitleTool.App.ViewModels;

namespace SubtitleTool.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private static IReadOnlyList<string> DroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] items
            ? items.Where(p => Directory.Exists(p) || SubtitleTool.Core.Media.VideoFormats.IsSupported(p)).ToList()
            : [];

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var paths = DroppedPaths(e);
        if (paths.Count > 0 && ViewModel is { } vm)
            await vm.AddPathsAsync(paths);
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is { SelectedVideo: { } row } vm)
            vm.OpenRowCommand.Execute(row);
    }
}
