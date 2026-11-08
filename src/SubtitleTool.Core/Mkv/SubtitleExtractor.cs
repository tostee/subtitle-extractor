using SubtitleTool.Core.Media;
using SubtitleTool.Core.Processes;

namespace SubtitleTool.Core.Mkv;

public enum ExtractionStatus
{
    Extracted,
    Skipped,
    Failed,
}

public sealed record ExtractionResult(ExtractionStatus Status, string OutputPath, string Message);

/// <summary>Extrae una pista con <c>mkvextract &lt;video&gt; tracks &lt;ID&gt;:&lt;salida&gt;</c>.</summary>
public sealed class SubtitleExtractor : ISubtitleTrackExtractor
{
    private readonly MkvToolNixPaths _paths;
    private readonly IProcessRunner _runner;

    public SubtitleExtractor(MkvToolNixPaths paths, IProcessRunner? runner = null)
    {
        _paths = paths;
        _runner = runner ?? new ProcessRunner();
    }

    public Task<ExtractionResult> ExtractAsync(string videoPath, int trackId, string outputPath, CancellationToken cancellationToken = default) =>
        SafeExtraction.RunAsync(outputPath,
            temp => _runner.RunAsync(_paths.MkvExtract, [videoPath, "tracks", $"{trackId}:{temp}"], cancellationToken),
            // mkvextract: 0 = ok, 1 = advertencias, 2 = error
            r => r.ExitCode >= 2 ? $"mkvextract falló (código {r.ExitCode}): {LastLines(Detail(r))}" : null,
            r => r.ExitCode == 1 ? "Extraído (con advertencias de mkvextract)." : "Extraído.",
            "mkvextract");

    internal static string Detail(ProcessResult r) => string.IsNullOrWhiteSpace(r.StandardError) ? r.StandardOutput : r.StandardError;

    internal static string LastLines(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines.TakeLast(3)).Trim();
    }
}
