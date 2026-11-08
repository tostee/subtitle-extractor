using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SubtitleTool.App.ViewModels;

namespace SubtitleTool.App.Views;

public partial class ReviewWindow : Window
{
    private bool _closingByViewModel;

    public ReviewWindow(ReviewViewModel viewModel)
    {
        InitializeComponent();
        WindowFit.Attach(this);
        DataContext = viewModel;
        viewModel.CloseRequested += () =>
        {
            _closingByViewModel = true;
            Close();
        };
        viewModel.FocusRowRequested += FocusSpanishOf;
    }

    private ReviewViewModel ViewModel => (ReviewViewModel)DataContext;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_closingByViewModel && !ViewModel.CanClose())
            e.Cancel = true;
    }

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

    /// <summary>Pone el cursor en el español de un bloque (tras hacerlo visible: la tabla está virtualizada).</summary>
    private void FocusSpanishOf(ReviewRowViewModel row)
    {
        BlocksGrid.ScrollIntoView(row);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (TryFocusEditor(row))
                return;
            // La fila aún no tiene contenedor (virtualización): se fuerza el diseño y se reintenta una vez más tarde.
            BlocksGrid.UpdateLayout();
            BlocksGrid.ScrollIntoView(row);
            if (!TryFocusEditor(row))
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () => TryFocusEditor(row));
        });
    }

    private bool TryFocusEditor(ReviewRowViewModel row)
    {
        if (BlocksGrid.ItemContainerGenerator.ContainerFromItem(row) is not DataGridRow container)
            return false;
        container.ApplyTemplate();
        if (VisualTreeSearch.FindDescendant<TextBox>(container) is not { } editor)
            return false;
        editor.Focus();
        editor.CaretIndex = editor.Text.Length;
        return true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Ctrl+F = ir a la búsqueda (F3 y Ctrl+S son KeyBinding de la ventana).
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
}
