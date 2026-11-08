using SubtitleTool.Core.Mkv;
using SubtitleTool.Core.Processes;

namespace SubtitleTool.Core.Media;

/// <summary>Lee las pistas de un video.</summary>
public interface IVideoInspector
{
    Task<MkvInfo> InspectAsync(string videoPath, CancellationToken cancellationToken = default);
}

/// <summary>Extrae una pista de subtítulos a un archivo (nunca sobrescribe).</summary>
public interface ISubtitleTrackExtractor
{
    Task<ExtractionResult> ExtractAsync(string videoPath, int trackId, string outputPath, CancellationToken cancellationToken = default);
}

/// <summary>Formatos de video admitidos: Matroska (MKVToolNix) y MP4 (FFmpeg).</summary>
public static class VideoFormats
{
    public static IReadOnlyList<string> MatroskaExtensions { get; } = [".mkv"];
    public static IReadOnlyList<string> Mp4Extensions { get; } = [".mp4", ".m4v"];
    public static IReadOnlyList<string> All { get; } = [.. MatroskaExtensions, .. Mp4Extensions];

    /// <summary>Texto para mensajes, p. ej. ".mkv, .mp4 o .m4v".</summary>
    public const string Description = ".mkv, .mp4 o .m4v";

    /// <summary>Filtro para el diálogo de abrir archivos.</summary>
    public const string DialogFilter =
        "Videos (*.mkv;*.mp4;*.m4v)|*.mkv;*.mp4;*.m4v|Matroska (*.mkv)|*.mkv|MP4 (*.mp4;*.m4v)|*.mp4;*.m4v";

    public static bool IsSupported(string path) => Has(path, All);
    public static bool IsMp4(string path) => Has(path, Mp4Extensions);

    private static bool Has(string path, IReadOnlyList<string> extensions)
    {
        var ext = Path.GetExtension(path);
        return extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Carpetas de herramientas elegidas por el usuario; null = autodetección.</summary>
public sealed record MediaToolDirectories(string? MkvToolNix = null, string? Ffmpeg = null)
{
    public static MediaToolDirectories Auto { get; } = new();
}

/// <summary>
/// Elige la herramienta según la extensión: MKVToolNix para .mkv y FFmpeg para .mp4/.m4v.
/// Cada herramienta se busca solo cuando hace falta, así que una carpeta solo con .mkv no necesita FFmpeg (ni al revés).
/// </summary>
/// <exception cref="MkvToolNixNotFoundException">Un .mkv sin MKVToolNix.</exception>
/// <exception cref="FfmpegNotFoundException">Un .mp4 sin FFmpeg.</exception>
public sealed class MediaToolkit : IVideoInspector, ISubtitleTrackExtractor
{
    private readonly MediaToolDirectories _directories;
    private readonly IProcessRunner _runner;
    private readonly Func<string?, MkvToolNixPaths> _locateMkv;
    private readonly Func<string?, FfmpegPaths> _locateFfmpeg;

    public MediaToolkit(MediaToolDirectories? directories = null, IProcessRunner? runner = null,
        Func<string?, MkvToolNixPaths>? locateMkv = null, Func<string?, FfmpegPaths>? locateFfmpeg = null)
    {
        _directories = directories ?? MediaToolDirectories.Auto;
        _runner = runner ?? new ProcessRunner();
        _locateMkv = locateMkv ?? (d => MkvToolNixLocator.LocatePreferring(d));
        _locateFfmpeg = locateFfmpeg ?? (d => FfmpegLocator.LocatePreferring(d));
    }

    public Task<MkvInfo> InspectAsync(string videoPath, CancellationToken cancellationToken = default) =>
        VideoFormats.IsMp4(videoPath)
            ? new FfprobeInspector(_locateFfmpeg(_directories.Ffmpeg), _runner).InspectAsync(videoPath, cancellationToken)
            : new MkvInspector(_locateMkv(_directories.MkvToolNix), _runner).InspectAsync(videoPath, cancellationToken);

    public Task<ExtractionResult> ExtractAsync(string videoPath, int trackId, string outputPath, CancellationToken cancellationToken = default) =>
        VideoFormats.IsMp4(videoPath)
            ? new FfmpegSubtitleExtractor(_locateFfmpeg(_directories.Ffmpeg), _runner).ExtractAsync(videoPath, trackId, outputPath, cancellationToken)
            : new SubtitleExtractor(_locateMkv(_directories.MkvToolNix), _runner).ExtractAsync(videoPath, trackId, outputPath, cancellationToken);
}
