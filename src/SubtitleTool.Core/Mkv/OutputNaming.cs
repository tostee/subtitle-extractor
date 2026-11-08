namespace SubtitleTool.Core.Mkv;

/// <summary>
/// Calcula nombres de salida: misma carpeta (o una alternativa) y mismo nombre base que el video.
/// SRT → .en.srt (.en.sdh.srt si SDH), ASS/SSA → .en.ass, WebVTT → .en.vtt.
/// Traducción esperada: .es.srt / .es.sdh.srt.
/// </summary>
public static class OutputNaming
{
    public static string ExtensionForCodec(string? codecId) => codecId?.ToUpperInvariant() switch
    {
        "S_TEXT/UTF8" => ".srt",
        "S_TEXT/ASS" or "S_TEXT/SSA" => ".ass",
        "S_TEXT/WEBVTT" => ".vtt",
        _ => throw new ArgumentException($"Codec de subtítulos no soportado para extracción: '{codecId}'.", nameof(codecId)),
    };

    private static string BaseWithoutExtension(string videoPath, string? outputDirectory)
    {
        var dir = outputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(videoPath)) ?? ".";
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(videoPath));
    }

    /// <summary>Ruta del subtítulo en inglés que se extraerá.</summary>
    public static string EnglishPath(string videoPath, string? codecId, SubtitleKind kind, string? outputDirectory = null)
    {
        var ext = ExtensionForCodec(codecId);
        var sdh = kind == SubtitleKind.Sdh ? ".sdh" : "";
        return BaseWithoutExtension(videoPath, outputDirectory) + ".en" + sdh + ext;
    }

    /// <summary>Ruta esperada de la traducción al español.</summary>
    public static string SpanishPath(string videoPath, SubtitleKind kind, string? outputDirectory = null)
    {
        var sdh = kind == SubtitleKind.Sdh ? ".sdh" : "";
        return BaseWithoutExtension(videoPath, outputDirectory) + ".es" + sdh + ".srt";
    }

    /// <summary>Todas las rutas posibles de traducción (.es.srt y .es.sdh.srt).</summary>
    public static IReadOnlyList<string> SpanishCandidates(string videoPath, string? outputDirectory = null) =>
    [
        SpanishPath(videoPath, SubtitleKind.Normal, outputDirectory),
        SpanishPath(videoPath, SubtitleKind.Sdh, outputDirectory),
    ];

    /// <summary>Ruta de un .srt con el mismo nombre que el video y sin código de idioma.</summary>
    public static string UntaggedSrtPath(string videoPath, string? outputDirectory = null) =>
        BaseWithoutExtension(videoPath, outputDirectory) + ".srt";
}
