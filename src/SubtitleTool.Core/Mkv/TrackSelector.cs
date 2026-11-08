namespace SubtitleTool.Core.Mkv;

/// <summary>Clase de pista de subtítulos elegida.</summary>
public enum SubtitleKind
{
    Normal,
    Sdh,
    Forced,
}

/// <summary>Motivo por el que no se eligió pista.</summary>
public enum SelectionFailure
{
    None,
    /// <summary>No hay pistas de subtítulos en inglés.</summary>
    NoEnglish,
    /// <summary>Hay subtítulos en inglés pero solo de imagen (PGS/VobSub/DVB): necesitan OCR.</summary>
    ImageOnly,
}

public sealed record TrackSelection(MkvTrack? Track, SubtitleKind Kind, SelectionFailure Failure)
{
    public bool Success => Track is not null;
    public static TrackSelection Fail(SelectionFailure failure) => new(null, SubtitleKind.Normal, failure);
}

/// <summary>Lógica pura para elegir la pista de subtítulos en inglés a extraer.</summary>
public static class TrackSelector
{
    public static readonly IReadOnlySet<string> TextCodecIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "S_TEXT/UTF8", "S_TEXT/ASS", "S_TEXT/SSA", "S_TEXT/WEBVTT",
    };

    public static readonly IReadOnlySet<string> ImageCodecIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "S_HDMV/PGS", "S_VOBSUB", "S_DVBSUB",
    };

    public static bool IsEnglish(MkvTrack t)
    {
        var lang = t.Language?.Trim();
        if (string.Equals(lang, "eng", StringComparison.OrdinalIgnoreCase) || string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase))
            return true;
        var ietf = t.LanguageIetf?.Trim();
        if (string.IsNullOrEmpty(ietf))
            return false;
        // "en", "en-US", "en-GB"... pero no "eng-xx" raro ni "enm" (inglés medio)
        return string.Equals(ietf, "en", StringComparison.OrdinalIgnoreCase)
               || ietf.StartsWith("en-", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTextCodec(MkvTrack t) => t.CodecId is not null && TextCodecIds.Contains(t.CodecId);

    public static bool IsImageCodec(MkvTrack t) => t.CodecId is not null && ImageCodecIds.Contains(t.CodecId);

    /// <summary>Clasifica una pista como Normal, SDH o Forzada.</summary>
    public static SubtitleKind Classify(MkvTrack t)
    {
        var name = t.TrackName ?? "";
        if (t.ForcedTrack || ContainsWord(name, "forced"))
            return SubtitleKind.Forced;
        if (t.HearingImpaired == true || ContainsWord(name, "sdh") || ContainsWord(name, "cc"))
            return SubtitleKind.Sdh;
        return SubtitleKind.Normal;
    }

    public static TrackSelection Select(IEnumerable<MkvTrack> tracks)
    {
        var englishSubs = tracks.Where(t => t.IsSubtitle && IsEnglish(t)).ToList();
        if (englishSubs.Count == 0)
            return TrackSelection.Fail(SelectionFailure.NoEnglish);

        var candidates = englishSubs.Where(IsTextCodec).ToList();
        if (candidates.Count == 0)
        {
            return englishSubs.Any(IsImageCodec)
                ? TrackSelection.Fail(SelectionFailure.ImageOnly)
                : TrackSelection.Fail(SelectionFailure.NoEnglish);
        }

        foreach (var kind in new[] { SubtitleKind.Normal, SubtitleKind.Sdh, SubtitleKind.Forced })
        {
            var best = candidates
                .Where(t => Classify(t) == kind)
                .OrderByDescending(t => t.DefaultTrack)
                .ThenBy(t => t.Id)
                .FirstOrDefault();
            if (best is not null)
                return new TrackSelection(best, kind, SelectionFailure.None);
        }

        // Inalcanzable: toda pista se clasifica en alguna de las tres clases.
        return TrackSelection.Fail(SelectionFailure.NoEnglish);
    }

    /// <summary>Busca <paramref name="word"/> como palabra (no como subcadena: "CC" no debe casar con "Accent").</summary>
    private static bool ContainsWord(string text, string word)
    {
        var idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
            var afterPos = idx + word.Length;
            var after = afterPos >= text.Length || !char.IsLetterOrDigit(text[afterPos]);
            if (before && after)
                return true;
            idx = afterPos;
        }
        return false;
    }
}
