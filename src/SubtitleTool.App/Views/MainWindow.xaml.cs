using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SubtitleTool.App.ViewModels;

namespace SubtitleTool.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        WindowFit.Attach(this);
        DataContextChanged += OnDataContextChanged;
        // Al desplegar el registro, se muestran los mensajes más recientes.
        LogList.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    VisualTreeSearch.FindDescendant<ScrollViewer>(LogList)?.ScrollToEnd());
        };
    }

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

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old)
            old.LogLines.CollectionChanged -= OnLogChanged;
        if (e.NewValue is MainViewModel vm)
            vm.LogLines.CollectionChanged += OnLogChanged;
    }

    /// <summary>
    /// Registro: si el usuario está al final de la lista, se sigue el último mensaje; si subió para leer algo, no se le mueve.
    /// </summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !LogList.IsVisible
            || VisualTreeSearch.FindDescendant<ScrollViewer>(LogList) is not { } scroll)
            return;
        var atEnd = scroll.VerticalOffset >= scroll.ScrollableHeight - 1;
        if (atEnd)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, scroll.ScrollToEnd);
    }
}
