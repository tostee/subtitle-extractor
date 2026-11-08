using System.Globalization;
using SubtitleTool.Core.Library;
using SubtitleTool.Core.Mkv;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Workflow;

/// <summary>Qué hacer con un video según su estado (común a <c>subtool run</c> y a la interfaz gráfica).</summary>
public enum VideoPlanKind
{
    /// <summary>mkvmerge no pudo inspeccionar el archivo.</summary>
    InspectError,
    /// <summary>Ya tiene .es.srt / .es.sdh.srt (y no se pidió forzar).</summary>
    AlreadyTranslated,
    NoEnglish,
    /// <summary>Hay inglés pero solo en imagen (PGS/VobSub): necesita OCR.</summary>
    ImageOnly,
    /// <summary>La pista es .ass/.vtt: solo se traducen .srt.</summary>
    UnsupportedFormat,
    /// <summary>Ya está traducida y espera revisión (borrador sin .es.srt).</summary>
    PendingReview,
    /// <summary>Extraer (si falta) y traducir.</summary>
    Process,
}

/// <param name="Reason">Motivo breve (para tablas de resumen) cuando no se procesa.</param>
public sealed record VideoPlan(VideoPlanKind Kind, string Reason, bool NeedsExtraction = false);

/// <summary>Decisiones y textos compartidos por la CLI y la interfaz gráfica.</summary>
public static class VideoWorkflow
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static VideoPlan Plan(VideoStatus st, bool force)
    {
        if (st.Error is not null)
            return new VideoPlan(VideoPlanKind.InspectError, "error al inspeccionar");
        if (st.SpanishExisting.Count > 0 && !force)
            return new VideoPlan(VideoPlanKind.AlreadyTranslated, "ya tiene " + string.Join(", ", st.SpanishExisting.Select(SpanishSuffix)));
        if (st.Selection is not { Track: not null } || st.EnglishPath is null)
        {
            return st.Selection?.Failure == SelectionFailure.ImageOnly
                ? new VideoPlan(VideoPlanKind.ImageOnly, "inglés solo en imagen (necesita OCR)")
                : new VideoPlan(VideoPlanKind.NoEnglish, "sin subtítulos en inglés");
        }
        if (st.ReviewPending && !force)
            return new VideoPlan(VideoPlanKind.PendingReview, "traducción pendiente de revisión");
        if (!st.EnglishPath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            return new VideoPlan(VideoPlanKind.UnsupportedFormat, $"formato {Path.GetExtension(st.EnglishPath)} no soportado para traducir");
        return new VideoPlan(VideoPlanKind.Process, "", NeedsExtraction: !st.EnglishExists);
    }

    private static string SpanishSuffix(string path) =>
        path.EndsWith(".es.sdh.srt", StringComparison.OrdinalIgnoreCase) ? ".es.sdh.srt" : ".es.srt";

    public static string KindLabel(SubtitleKind kind) => kind switch
    {
        SubtitleKind.Normal => "normal",
        SubtitleKind.Sdh => "SDH",
        SubtitleKind.Forced => "forzada",
        _ => kind.ToString(),
    };

    /// <summary>Formato del coste: con Claude Code no se paga por uso, se muestra el equivalente de la API.</summary>
    public static string FormatCost(decimal usd, bool claudeCode) => claudeCode
        ? $"incluido en la suscripción (equivalente API ~${usd.ToString("0.0000", Inv)})"
        : $"coste estimado ${usd.ToString("0.0000", Inv)}";

    /// <summary>Detalle breve del resultado de una traducción (tabla de resumen).</summary>
    public static string ResultDetail(TranslationResult r, bool claudeCode) => r.Status switch
    {
        TranslationStatus.Completed => claudeCode
            ? $"{r.BlockCount} bloques, suscripción (≈${r.EstimatedCostUsd.ToString("0.00", Inv)} API)"
            : $"{r.BlockCount} bloques, ${r.EstimatedCostUsd.ToString("0.00", Inv)}",
        TranslationStatus.PendingReview => r.BlockCount > 0
            ? $"{r.BlockCount} bloques, lista para revisar" + (claudeCode ? "" : $" (${r.EstimatedCostUsd.ToString("0.00", Inv)})")
            : "pendiente de revisión",
        TranslationStatus.Failed => r.FailedBlocks.Count > 0 ? $"{r.FailedBlocks.Count} bloque(s) fallidos" : "validación con errores",
        _ => r.Message,
    };

    /// <summary>true si no tiene sentido seguir con más videos (límite de uso o sesión no iniciada).</summary>
    public static bool StopsBatch(TranslationClientException ex) => ex.IsUsageLimit || ex.IsAuthentication;
}
