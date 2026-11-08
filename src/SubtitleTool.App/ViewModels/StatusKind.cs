namespace SubtitleTool.App.ViewModels;

/// <summary>
/// Tipo de un estado o mensaje. Las ViewModels exponen el tipo y el texto por separado; la vista pinta el icono
/// (Segoe Fluent Icons) y su color (Views/StatusIcons). El texto siempre acompaña: el icono nunca es la única señal.
/// </summary>
public enum StatusKind
{
    /// <summary>Sin icono.</summary>
    None,
    Success,
    Error,
    Warning,
    Info,
    /// <summary>Traducción lista que espera tu revisión.</summary>
    Review,
    /// <summary>Bloque corregido a mano en la revisión.</summary>
    Edited,
    Skipped,
    Cancelled,
    /// <summary>En pausa (p. ej. límite de uso alcanzado).</summary>
    Paused,
    InProgress,
    Pending,
    Discarded,
    Update,
}

public static class StatusKinds
{
    /// <summary>Nombre del estado en palabras (registro en texto, nombre accesible, columna «Estado»).</summary>
    public static string Label(StatusKind kind) => kind switch
    {
        StatusKind.Success => "hecho",
        StatusKind.Error => "error",
        StatusKind.Warning => "aviso",
        StatusKind.Info => "información",
        StatusKind.Review => "por revisar",
        StatusKind.Edited => "editado",
        StatusKind.Skipped => "saltado",
        StatusKind.Cancelled => "cancelado",
        StatusKind.Paused => "en pausa",
        StatusKind.InProgress => "en curso",
        StatusKind.Pending => "pendiente",
        StatusKind.Discarded => "descartado",
        StatusKind.Update => "actualización",
        _ => "",
    };

    /// <summary>Prefijo para registros de texto plano (archivo, ventana de texto): «[error] », «[aviso] »…; vacío si no hay tipo.</summary>
    public static string TextPrefix(StatusKind kind) => kind == StatusKind.None ? "" : $"[{Label(kind)}] ";
}

/// <summary>Una línea del registro de la ventana principal: tipo (icono) y texto (con la hora).</summary>
public sealed record LogEntry(StatusKind Kind, string Text)
{
    /// <summary>Texto para Narrador (y para copiar): el tipo en palabras delante.</summary>
    public string SpokenText => StatusKinds.TextPrefix(Kind) + Text;

    public override string ToString() => SpokenText;
}

/// <summary>Un aviso de un bloque en la revisión (error o advertencia) con su texto breve.</summary>
public sealed record IssueItem(StatusKind Kind, string Text)
{
    /// <summary>Tipo en palabras (nombre accesible del icono).</summary>
    public string Label => Kind == StatusKind.Error ? "error" : "aviso";

    public override string ToString() => (Kind == StatusKind.Error ? "Error: " : "Aviso: ") + Text;
}

/// <summary>Resultado de una comprobación de Ajustes («Comprobar herramientas»).</summary>
public sealed record CheckRow(StatusKind Kind, string Text)
{
    /// <summary>Resultado en palabras (nombre accesible del icono).</summary>
    public string Label => Kind switch
    {
        StatusKind.Success => "correcto",
        StatusKind.Warning => "opcional, no encontrado",
        _ => "falla",
    };

    public override string ToString() => StatusKinds.TextPrefix(Kind) + Text;
}
