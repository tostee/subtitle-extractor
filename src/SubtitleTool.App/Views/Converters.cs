using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SubtitleTool.App.Views;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Color de los textos de estado: ✅ verde, ❌ rojo, "en curso" azul; el resto, color normal.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly System.Windows.Media.Brush Ok = Frozen(0x1A, 0x7F, 0x37);
    private static readonly System.Windows.Media.Brush Bad = Frozen(0xB4, 0x23, 0x18);
    private static readonly System.Windows.Media.Brush Busy = Frozen(0x1D, 0x4E, 0xD8);
    private static readonly System.Windows.Media.Brush Normal = Frozen(0x1F, 0x23, 0x28);

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        { } s when s.StartsWith('✅') => Ok,
        { } s when s.StartsWith('❌') => Bad,
        "en curso" => Busy,
        { } s when s.StartsWith("📝", StringComparison.Ordinal) => Busy,
        _ => Normal,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
