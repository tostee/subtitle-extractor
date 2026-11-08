using SubtitleTool.Core.Mkv;
using SubtitleTool.Core.Processes;

namespace SubtitleTool.Core.Media;

/// <summary>
/// Extrae una pista de subtítulos de texto de un .mp4 con
/// <c>ffmpeg -n -i &lt;video&gt; -map 0:&lt;índice&gt; -c:s srt|ass|webvtt &lt;salida&gt;</c>
/// (mov_text → SRT). El formato de salida se deduce de la extensión.
/// </summary>
public sealed class FfmpegSubtitleExtractor : ISubtitleTrackExtractor
{
    private readonly FfmpegPaths _paths;
    private readonly IProcessRunner _runner;

    public FfmpegSubtitleExtractor(FfmpegPaths paths, IProcessRunner? runner = null)
    {
        _paths = paths;
        _runner = runner ?? new ProcessRunner();
    }

    public static string EncoderFor(string outputPath) => Path.GetExtension(outputPath).ToLowerInvariant() switch
    {
        ".srt" => "srt",
        ".ass" => "ass",
        ".vtt" => "webvtt",
        var ext => throw new ArgumentException($"Formato de salida no soportado por ffmpeg: '{ext}'.", nameof(outputPath)),
    };

    public static IReadOnlyList<string> BuildArguments(string videoPath, int streamIndex, string outputPath) =>
    [
        "-nostdin", "-hide_banner", "-loglevel", "error",
        "-n", // nunca sobrescribir
        "-i", videoPath,
        "-map", $"0:{streamIndex}",
        "-c:s", EncoderFor(outputPath),
        outputPath,
    ];

    public Task<ExtractionResult> ExtractAsync(string videoPath, int trackId, string outputPath, CancellationToken cancellationToken = default)
    {
        EncoderFor(outputPath); // valida el formato antes de lanzar nada
        return SafeExtraction.RunAsync(outputPath,
            async temp =>
            {
                try
                {
                    return await _runner.RunAsync(_paths.FFmpeg, BuildArguments(videoPath, trackId, temp), cancellationToken).ConfigureAwait(false);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // ffmpeg.exe encontrado pero no se puede ejecutar (enlace roto, antivirus…): falla solo este video.
                    return new ProcessResult(-1, "", $"no se pudo ejecutar {_paths.FFmpeg}: {ex.Message}");
                }
            },
            r => r.ExitCode != 0 ? $"ffmpeg falló (código {r.ExitCode}): {SubtitleExtractor.LastLines(SubtitleExtractor.Detail(r))}" : null,
            _ => "Extraído (ffmpeg).",
            "ffmpeg");
    }
}
