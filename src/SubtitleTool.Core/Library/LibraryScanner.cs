using SubtitleTool.Core.Media;
using SubtitleTool.Core.Mkv;

namespace SubtitleTool.Core.Library;

/// <summary>Estado de un video de la biblioteca.</summary>
public sealed record VideoStatus
{
    public required string VideoPath { get; init; }
    public IReadOnlyList<MkvTrack> SubtitleTracks { get; init; } = [];
    public TrackSelection? Selection { get; init; }
    /// <summary>Ruta donde está (o estaría) el subtítulo en inglés; null si no hay pista elegible.</summary>
    public string? EnglishPath { get; init; }
    public bool EnglishExists { get; init; }
    /// <summary>Traducciones existentes (.es.srt / .es.sdh.srt).</summary>
    public IReadOnlyList<string> SpanishExisting { get; init; } = [];
    /// <summary>.srt con el mismo nombre que el video y sin código de idioma (no se supone qué contiene).</summary>
    public string? UntaggedSrt { get; init; }
    /// <summary>Error al inspeccionar el archivo (mkvmerge/ffprobe falló, falta FFmpeg para un .mp4, etc.).</summary>
    public string? Error { get; init; }
}

/// <summary>Recorre un archivo o carpeta (recursivo) y devuelve el estado de cada video (.mkv, .mp4, .m4v).</summary>
public sealed class LibraryScanner
{
    private readonly IVideoInspector _inspector;

    public LibraryScanner(IVideoInspector inspector) => _inspector = inspector;

    /// <summary>Lista los videos (.mkv, .mp4, .m4v) de una ruta (archivo o carpeta recursiva), ordenados por ruta.</summary>
    public static IReadOnlyList<string> FindVideos(string path)
    {
        if (File.Exists(path))
            return VideoFormats.IsSupported(path) ? [Path.GetFullPath(path)] : [];
        if (Directory.Exists(path))
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            };
            return Directory.EnumerateFiles(path, "*", options)
                .Where(VideoFormats.IsSupported)
                .Select(Path.GetFullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        throw new FileNotFoundException($"No existe el archivo o carpeta: {path}", path);
    }

    public async Task<IReadOnlyList<VideoStatus>> ScanAsync(string path, string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        var results = new List<VideoStatus>();
        foreach (var video in FindVideos(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ScanVideoAsync(video, outputDirectory, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    public async Task<VideoStatus> ScanVideoAsync(string videoPath, string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        // Lo que ya está junto al video (traducción, .srt sin idioma) se busca siempre al lado del video.
        var spanish = OutputNaming.SpanishCandidates(videoPath).Where(File.Exists).ToList();
        var untagged = OutputNaming.UntaggedSrtPath(videoPath);
        var untaggedSrt = File.Exists(untagged) ? untagged : null;

        MkvInfo info;
        try
        {
            info = await _inspector.InspectAsync(videoPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MkvToolNixException or FfmpegException or FfmpegNotFoundException)
        {
            // Sin FFmpeg solo fallan los .mp4 (cada uno con su aviso); sin MKVToolNix se propaga: afecta a todos los .mkv.
            return new VideoStatus
            {
                VideoPath = videoPath,
                SpanishExisting = spanish,
                UntaggedSrt = untaggedSrt,
                Error = ex.Message,
            };
        }

        var selection = TrackSelector.Select(info.Tracks);
        string? englishPath = null;
        if (selection.Track is { } track)
            englishPath = OutputNaming.EnglishPath(videoPath, track.CodecId, selection.Kind, outputDirectory);

        return new VideoStatus
        {
            VideoPath = videoPath,
            SubtitleTracks = info.Tracks.Where(t => t.IsSubtitle).ToList(),
            Selection = selection,
            EnglishPath = englishPath,
            EnglishExists = englishPath is not null && File.Exists(englishPath),
            SpanishExisting = spanish,
            UntaggedSrt = untaggedSrt,
        };
    }
}
