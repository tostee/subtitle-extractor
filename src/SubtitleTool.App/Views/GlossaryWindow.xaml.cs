using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SubtitleTool.App.ViewModels;

namespace SubtitleTool.App.Views;

public partial class GlossaryWindow : Window
{
    public GlossaryWindow()
    {
        InitializeComponent();
        WindowFit.Attach(this);
    }

    /// <summary>
    /// Antes de Guardar o Regenerar (el Click llega antes que el comando): confirma la celda y la fila en edición,
    /// incluida una fila nueva todavía sin confirmar, para que cuente.
    /// </summary>
    private void OnCommitEdits(object sender, RoutedEventArgs e) => CommitEdits();

    private void CommitEdits()
    {
        foreach (var grid in new[] { TermsGrid, AddressGrid })
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
    }

    /// <summary>Cambios sin guardar o generación en curso: decide la ViewModel (pregunta al usuario).</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Confirma la celda que se esté editando para que cuente como cambio.
        CommitEdits();
        if (DataContext is GlossaryEditorViewModel vm && !vm.CanClose())
            e.Cancel = true;
    }
}
