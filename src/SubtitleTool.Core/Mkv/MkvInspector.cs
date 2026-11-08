using System.Text.Json;
using System.Text.Json.Serialization;
using SubtitleTool.Core.Media;
using SubtitleTool.Core.Processes;

namespace SubtitleTool.Core.Mkv;

public sealed class MkvToolNixException : Exception
{
    public MkvToolNixException(string message) : base(message) { }
}

/// <summary>Ejecuta <c>mkvmerge -J</c> y deserializa las pistas.</summary>
public sealed class MkvInspector : IVideoInspector
{
    private readonly MkvToolNixPaths _paths;
    private readonly IProcessRunner _runner;

    public MkvInspector(MkvToolNixPaths paths, IProcessRunner? runner = null)
    {
        _paths = paths;
        _runner = runner ?? new ProcessRunner();
    }

    public async Task<MkvInfo> InspectAsync(string mkvPath, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_paths.MkvMerge, ["-J", mkvPath], cancellationToken).ConfigureAwait(false);
        // mkvmerge: 0 = ok, 1 = advertencias, 2 = error
        if (result.ExitCode >= 2 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new MkvToolNixException($"mkvmerge falló (código {result.ExitCode}) con '{mkvPath}': {detail.Trim()}");
        }
        return new MkvInfo(mkvPath, ParseTracks(result.StandardOutput));
    }

    /// <summary>Convierte la salida JSON de <c>mkvmerge -J</c> en pistas.</summary>
    public static IReadOnlyList<MkvTrack> ParseTracks(string json)
    {
        var doc = JsonSerializer.Deserialize<IdentifyDto>(json)
                  ?? throw new MkvToolNixException("La salida de mkvmerge -J está vacía.");
        if (doc.Errors is { Count: > 0 })
            throw new MkvToolNixException("mkvmerge informó errores: " + string.Join("; ", doc.Errors));

        return (doc.Tracks ?? []).Select(t => new MkvTrack
        {
            Id = t.Id,
            Type = t.Type ?? "",
            Codec = t.Codec ?? "",
            CodecId = t.Properties?.CodecId,
            Language = t.Properties?.Language,
            LanguageIetf = t.Properties?.LanguageIetf,
            TrackName = t.Properties?.TrackName,
            ForcedTrack = t.Properties?.ForcedTrack ?? false,
            DefaultTrack = t.Properties?.DefaultTrack ?? false,
            HearingImpaired = t.Properties?.FlagHearingImpaired,
        }).ToList();
    }

    private sealed class IdentifyDto
    {
        [JsonPropertyName("tracks")] public List<TrackDto>? Tracks { get; set; }
        [JsonPropertyName("errors")] public List<string>? Errors { get; set; }
    }

    private sealed class TrackDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("codec")] public string? Codec { get; set; }
        [JsonPropertyName("properties")] public PropertiesDto? Properties { get; set; }
    }

    private sealed class PropertiesDto
    {
        [JsonPropertyName("codec_id")] public string? CodecId { get; set; }
        [JsonPropertyName("language")] public string? Language { get; set; }
        [JsonPropertyName("language_ietf")] public string? LanguageIetf { get; set; }
        [JsonPropertyName("track_name")] public string? TrackName { get; set; }
        [JsonPropertyName("forced_track")] public bool? ForcedTrack { get; set; }
        [JsonPropertyName("default_track")] public bool? DefaultTrack { get; set; }
        [JsonPropertyName("flag_hearing_impaired")] public bool? FlagHearingImpaired { get; set; }
    }
}
