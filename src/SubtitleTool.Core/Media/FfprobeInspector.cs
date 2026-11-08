using System.Text.Json;
using System.Text.Json.Serialization;
using SubtitleTool.Core.Mkv;
using SubtitleTool.Core.Processes;

namespace SubtitleTool.Core.Media;

public sealed class FfmpegException : Exception
{
    public FfmpegException(string message) : base(message) { }
}

/// <summary>
/// Ejecuta <c>ffprobe -show_streams</c> sobre un .mp4/.m4v y traduce las pistas al mismo modelo que usa MKVToolNix
/// (<see cref="MkvTrack"/> con codec_id estilo Matroska), para reutilizar <see cref="TrackSelector"/> y <see cref="OutputNaming"/>.
/// El <see cref="MkvTrack.Id"/> es el índice de stream de ffmpeg (el que se usa en <c>-map 0:N</c>).
/// </summary>
public sealed class FfprobeInspector : IVideoInspector
{
    private readonly FfmpegPaths _paths;
    private readonly IProcessRunner _runner;

    public FfprobeInspector(FfmpegPaths paths, IProcessRunner? runner = null)
    {
        _paths = paths;
        _runner = runner ?? new ProcessRunner();
    }

    public async Task<MkvInfo> InspectAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_paths.FFprobe,
                ["-v", "error", "-print_format", "json", "-show_streams", videoPath], cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // ffprobe.exe encontrado pero no se puede ejecutar (enlace roto, antivirus…): falla solo este video.
            throw new FfmpegException($"No se pudo ejecutar {_paths.FFprobe}: {ex.Message}");
        }
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new FfmpegException($"ffprobe falló (código {result.ExitCode}) con '{videoPath}': {detail.Trim()}");
        }
        return new MkvInfo(videoPath, ParseTracks(result.StandardOutput));
    }

    /// <summary>Codec de ffmpeg → codec_id de Matroska (los de texto se extraen a .srt/.ass/.vtt).</summary>
    public static string MapCodecId(string? codecName) => codecName?.ToLowerInvariant() switch
    {
        "mov_text" or "subrip" or "srt" or "text" => "S_TEXT/UTF8",
        "ass" or "ssa" => "S_TEXT/ASS",
        "webvtt" => "S_TEXT/WEBVTT",
        "hdmv_pgs_subtitle" => "S_HDMV/PGS",
        "dvd_subtitle" => "S_VOBSUB",
        "dvb_subtitle" => "S_DVBSUB",
        null or "" => "",
        var other => "FFMPEG/" + other,
    };

    /// <summary>Convierte la salida JSON de <c>ffprobe -print_format json -show_streams</c> en pistas.</summary>
    public static IReadOnlyList<MkvTrack> ParseTracks(string json)
    {
        ProbeDto? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ProbeDto>(json);
        }
        catch (JsonException ex)
        {
            throw new FfmpegException("La salida de ffprobe no es JSON válido: " + ex.Message);
        }
        if (doc is null)
            throw new FfmpegException("La salida de ffprobe está vacía.");

        return (doc.Streams ?? []).Select(s =>
        {
            var type = s.CodecType?.ToLowerInvariant() switch
            {
                "subtitle" => "subtitles",
                null => "",
                var t => t,
            };
            var tags = s.Tags is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(s.Tags, StringComparer.OrdinalIgnoreCase);
            var language = tags.GetValueOrDefault("language");
            return new MkvTrack
            {
                Id = s.Index,
                Type = type,
                Codec = s.CodecName ?? "",
                CodecId = type == "subtitles" ? MapCodecId(s.CodecName) : s.CodecName,
                Language = string.Equals(language, "und", StringComparison.OrdinalIgnoreCase) ? null : language,
                TrackName = TrackName(tags),
                ForcedTrack = s.Disposition?.GetValueOrDefault("forced") == 1,
                DefaultTrack = s.Disposition?.GetValueOrDefault("default") == 1,
                HearingImpaired = s.Disposition?.GetValueOrDefault("hearing_impaired") == 1 ? true : null,
            };
        }).ToList();
    }

    /// <summary>Título de la pista; el handler_name genérico de los .mp4 ("SubtitleHandler") no cuenta como nombre.</summary>
    private static string? TrackName(Dictionary<string, string> tags)
    {
        // En .mkv el título va en "title"; en .mp4 ffprobe lo muestra como "name" (átomo udta/name).
        foreach (var key in new[] { "title", "name" })
        {
            if (tags.TryGetValue(key, out var title) && !string.IsNullOrWhiteSpace(title))
                return title.Trim();
        }
        if (tags.TryGetValue("handler_name", out var handler) && !string.IsNullOrWhiteSpace(handler)
            && !handler.Trim().EndsWith("Handler", StringComparison.OrdinalIgnoreCase))
            return handler.Trim();
        return null;
    }

    private sealed class ProbeDto
    {
        [JsonPropertyName("streams")] public List<StreamDto>? Streams { get; set; }
    }

    private sealed class StreamDto
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("codec_name")] public string? CodecName { get; set; }
        [JsonPropertyName("codec_type")] public string? CodecType { get; set; }
        [JsonPropertyName("disposition")] public Dictionary<string, int>? Disposition { get; set; }
        [JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }
    }
}
