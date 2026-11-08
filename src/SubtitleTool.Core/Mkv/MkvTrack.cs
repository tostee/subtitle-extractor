namespace SubtitleTool.Core.Mkv;

/// <summary>Pista de un contenedor Matroska tal como la describe <c>mkvmerge -J</c>.</summary>
public sealed record MkvTrack
{
    public int Id { get; init; }
    public string Type { get; init; } = "";
    public string Codec { get; init; } = "";
    public string? CodecId { get; init; }
    public string? Language { get; init; }
    public string? LanguageIetf { get; init; }
    public string? TrackName { get; init; }
    public bool ForcedTrack { get; init; }
    public bool DefaultTrack { get; init; }
    public bool? HearingImpaired { get; init; }

    public bool IsSubtitle => string.Equals(Type, "subtitles", StringComparison.OrdinalIgnoreCase);
}

public sealed record MkvInfo(string FilePath, IReadOnlyList<MkvTrack> Tracks);
