using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using SubtitleTool.App.ViewModels;

namespace SubtitleTool.App.Views;

/// <summary>
/// Iconos de estado: único sitio donde se eligen glifo (Segoe Fluent Icons; Segoe MDL2 Assets en Windows 10, mismos
/// códigos) y color (clave semántica de Themes/Styles.xaml, que sigue al alto contraste).
/// </summary>
public static class StatusIcons
{
    public const string FontFamilyName = "Segoe Fluent Icons, Segoe MDL2 Assets";

    public static readonly FontFamily IconFont = new(FontFamilyName);

    /// <summary>Glifo del tipo de estado (vacío = sin icono).</summary>
    public static string Glyph(StatusKind kind) => kind switch
    {
        StatusKind.Success => "",     // CheckMark
        StatusKind.Error => "",       // ErrorBadge
        StatusKind.Warning => "",     // Warning
        StatusKind.Info => "",        // Info
        StatusKind.Review => "",      // Edit
        StatusKind.Edited => "",      // Edit
        StatusKind.Skipped => "",     // Next
        StatusKind.Cancelled => "",   // Pause
        StatusKind.Paused => "",      // Pause
        StatusKind.InProgress => "",  // Sync
        StatusKind.Pending => "",     // Recent
        StatusKind.Discarded => "",   // Delete
        StatusKind.Update => "",      // UpdateRestore
        _ => "",
    };

    /// <summary>Clave de la paleta para el color del icono.</summary>
    public static string BrushKey(StatusKind kind) => kind switch
    {
        StatusKind.Success => "SuccessText",
        StatusKind.Error => "ErrorText",
        StatusKind.Warning or StatusKind.Review or StatusKind.Edited or StatusKind.Paused => "WarningText",
        StatusKind.Info or StatusKind.InProgress or StatusKind.Update => "InfoText",
        _ => "MutedText",
    };
}

/// <summary>
/// Icono de estado (glifo de Segoe Fluent Icons con el color de la paleta). Es decorativo: no aparece en el árbol de
/// accesibilidad, porque siempre va junto a un texto que dice lo mismo. Si se usa solo, hay que darle
/// <see cref="AutomationProperties.NameProperty"/> y entonces sí se expone.
/// </summary>
public sealed class StatusIcon : TextBlock
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StatusKind), typeof(StatusIcon), new PropertyMetadata(StatusKind.None, (d, _) => ((StatusIcon)d).Update()));

    public StatusIcon()
    {
        // En código y no en un estilo implícito: los estilos implícitos de TextBlock no llegan dentro de las plantillas.
        FontFamily = StatusIcons.IconFont;
        FontSize = 14;
        FontWeight = FontWeights.Normal;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(0, 0, 6, 0);
        Style = ColorStyle;
        Update();
    }

    /// <summary>
    /// Color según el tipo: un estilo con un disparador por tipo y el color con DynamicResource, el mismo mecanismo
    /// que el resto de la paleta, así que sigue al alto contraste en caliente. Se genera desde StatusIcons.BrushKey
    /// (una sola tabla) y uno por hilo de UI, porque un estilo pertenece a su Dispatcher.
    /// </summary>
    [ThreadStatic] private static Style? _colorStyle;

    private static Style ColorStyle => _colorStyle ??= BuildColorStyle();

    private static Style BuildColorStyle()
    {
        var style = new Style(typeof(StatusIcon));
        style.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension(StatusIcons.BrushKey(StatusKind.None))));
        foreach (var kind in Enum.GetValues<StatusKind>().Where(k => k != StatusKind.None))
        {
            var trigger = new Trigger { Property = KindProperty, Value = kind };
            trigger.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension(StatusIcons.BrushKey(kind))));
            style.Triggers.Add(trigger);
        }
        style.Seal();
        return style;
    }

    public StatusKind Kind
    {
        get => (StatusKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private void Update()
    {
        Text = StatusIcons.Glyph(Kind);
        Visibility = Kind == StatusKind.None ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override AutomationPeer? OnCreateAutomationPeer() =>
        string.IsNullOrEmpty(AutomationProperties.GetName(this)) ? null : base.OnCreateAutomationPeer();
}
