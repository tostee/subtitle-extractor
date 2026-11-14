using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SubtitleTool.App.ViewModels;

namespace SubtitleTool.App.Views;

public partial class DubbingWindow : Window
{
    public DubbingWindow(DubbingViewModel viewModel)
    {
        InitializeComponent();
        WindowFit.Attach(this);
        DataContext = viewModel;
        // El registro sigue al último mensaje.
        viewModel.LogLines.CollectionChanged += OnLogChanged;
        Loaded += async (_, _) => await viewModel.DetectGpuAsync();
    }

    private DubbingViewModel ViewModel => (DubbingViewModel)DataContext;

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !LogList.IsVisible)
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            VisualTreeSearch.FindDescendant<ScrollViewer>(LogList)?.ScrollToEnd());
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!ViewModel.CanClose())
            e.Cancel = true;
        else
            ViewModel.LogLines.CollectionChanged -= OnLogChanged;
    }
}
