using System.Windows;
using System.Windows.Media;

namespace SubtitleTool.App.Views;

public partial class TextWindow : Window
{
    /// <param name="prose">Texto corrido (p. ej. notas de versión): fuente normal y ajuste de línea. Si no, registro en Consolas.</param>
    public TextWindow(bool prose = false)
    {
        InitializeComponent();
        WindowFit.Attach(this);
        if (prose)
        {
            ContentBox.FontFamily = new FontFamily("Segoe UI");
            ContentBox.FontSize = 13;
            ContentBox.TextWrapping = TextWrapping.Wrap;
            ContentBox.HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
        }
    }
}
