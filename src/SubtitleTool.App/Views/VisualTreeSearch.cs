using System.Windows;
using System.Windows.Media;

namespace SubtitleTool.App.Views;

/// <summary>Búsquedas en el árbol visual (para asuntos de vista: foco, desplazamiento).</summary>
public static class VisualTreeSearch
{
    /// <summary>Primer descendiente del tipo indicado (en profundidad), o null.</summary>
    public static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;
            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }
        return null;
    }
}
