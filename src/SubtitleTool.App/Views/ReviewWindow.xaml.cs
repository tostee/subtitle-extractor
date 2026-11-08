using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SubtitleTool.App.ViewModels;

namespace SubtitleTool.App.Views;

public partial class ReviewWindow : Window
{
    private bool _closingByViewModel;

    public ReviewWindow(ReviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += () =>
        {
            _closingByViewModel = true;
            Close();
        };
    }

    private ReviewViewModel ViewModel => (ReviewViewModel)DataContext;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_closingByViewModel && !ViewModel.CanClose())
            e.Cancel = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>«Siguiente aviso» cambia la selección: se lleva la fila a la vista.</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BlocksGrid.SelectedItem is { } item)
            BlocksGrid.ScrollIntoView(item);
    }

    /// <summary>Al escribir en una celda, esa fila pasa a ser la seleccionada (para que «Siguiente aviso» siga desde ahí).</summary>
    private void OnEditorFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReviewRowViewModel row } && !ReferenceEquals(ViewModel.SelectedRow, row))
            ViewModel.SelectedRow = row;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // F3 = siguiente aviso.
        if (e.Key == Key.F3)
        {
            ViewModel.NextIssueCommand.Execute(null);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

}
