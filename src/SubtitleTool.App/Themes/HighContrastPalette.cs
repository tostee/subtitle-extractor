using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace SubtitleTool.App.Themes;

/// <summary>
/// Alto contraste: mientras está activo, las claves de la paleta (Themes/Styles.xaml) se sustituyen por los colores
/// del sistema. Las vistas usan <c>DynamicResource</c>, así que el cambio se aplica al momento, también si el usuario
/// activa o cambia el tema de alto contraste con la app abierta.
/// </summary>
public static class HighContrastPalette
{
    /// <summary>Clave de la paleta → color del sistema que la sustituye.</summary>
    internal static readonly IReadOnlyList<(string Key, Func<Brush> System)> Map =
    [
        ("WindowBackground", () => SystemColors.WindowBrush),
        ("Surface", () => SystemColors.WindowBrush),
        ("Border", () => SystemColors.WindowTextBrush),
        ("Text", () => SystemColors.WindowTextBrush),
        ("MutedText", () => SystemColors.WindowTextBrush),
        ("Accent", () => SystemColors.HighlightBrush),
        ("AccentHover", () => SystemColors.HighlightBrush),
        ("AccentPressed", () => SystemColors.HighlightBrush),
        ("OnAccentText", () => SystemColors.HighlightTextBrush),
        ("FocusBorder", () => SystemColors.WindowTextBrush),
        ("ButtonHover", () => SystemColors.WindowBrush),
        ("ButtonPressed", () => SystemColors.WindowBrush),
        // La selección se marca con el borde de la fila: el texto conserva su color sobre el fondo de ventana.
        ("RowSelected", () => SystemColors.WindowBrush),
        ("RowSelectedBorder", () => SystemColors.HighlightBrush),
        ("RowHover", () => SystemColors.WindowBrush),
        ("HeaderBackground", () => SystemColors.WindowBrush),
        ("GridLine", () => SystemColors.GrayTextBrush),
        ("TrackBackground", () => SystemColors.WindowBrush),
        ("ReadOnlyBackground", () => SystemColors.WindowBrush),
        ("SuccessText", () => SystemColors.WindowTextBrush),
        ("ErrorText", () => SystemColors.WindowTextBrush),
        ("WarningText", () => SystemColors.WindowTextBrush),
        ("InfoText", () => SystemColors.WindowTextBrush),
        ("ErrorBorder", () => SystemColors.HighlightBrush),
        ("EditedBackground", () => SystemColors.WindowBrush),
        ("InfoBannerBackground", () => SystemColors.WindowBrush),
    ];

    /// <summary>Aplica la paleta actual y se suscribe a los cambios de tema del sistema.</summary>
    public static void Attach(Application app)
    {
        Apply(app);
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
                app.Dispatcher.BeginInvoke(() => Apply(app));
        };
        // Cambiar de un tema de alto contraste a otro no cambia HighContrast, pero sí los colores del sistema.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility or UserPreferenceCategory.General)
                app.Dispatcher.BeginInvoke(() => Apply(app));
        };
    }

    /// <summary>Con alto contraste, la paleta toma los colores del sistema; sin él, recupera los de Styles.xaml.</summary>
    public static void Apply(Application app) => Apply(app.Resources, SystemParameters.HighContrast);

    /// <summary>Diccionario con los colores de alto contraste (se reconoce por el tipo para quitarlo o sustituirlo).</summary>
    internal sealed class HighContrastDictionary : ResourceDictionary
    {
    }

    /// <summary>
    /// Pone (o vacía) un diccionario fusionado con todos los colores de sistema, por encima de Styles.xaml: un único
    /// cambio de diccionario refresca todas las referencias dinámicas de una vez, en lugar de una pasada por el árbol
    /// por cada clave. Solo toca lo que cambió: sin alto contraste y sin colores aplicados no hace nada; con alto
    /// contraste y los mismos colores de sistema, tampoco.
    /// </summary>
    internal static void Apply(ResourceDictionary resources, bool highContrast)
    {
        var current = resources.MergedDictionaries.OfType<HighContrastDictionary>().FirstOrDefault();
        if (!highContrast)
        {
            // Se sustituye por uno vacío (en vez de quitarlo) para conservar su posición en la lista.
            if (current is { Count: > 0 })
                resources.MergedDictionaries[resources.MergedDictionaries.IndexOf(current)] = new HighContrastDictionary();
            return;
        }
        var brushes = Map.Select(m => (m.Key, Brush: m.System())).ToList();
        if (current is not null && brushes.All(b => current.Contains(b.Key) && SameColor(current[b.Key], b.Brush)))
            return;
        var next = new HighContrastDictionary();
        foreach (var (key, brush) in brushes)
            next[key] = brush;
        if (current is null)
            resources.MergedDictionaries.Add(next);   // el último fusionado gana sobre Styles.xaml
        else
            resources.MergedDictionaries[resources.MergedDictionaries.IndexOf(current)] = next;
    }

    private static bool SameColor(object? a, Brush b) =>
        a is SolidColorBrush x && b is SolidColorBrush y && x.Color == y.Color;
}
