using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SubtitleTool.Core.Library;
using SubtitleTool.Core.Mkv;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.App.ViewModels;

public enum RowState
{
    Scanning,
    Ready,
    Queued,
    Processing,
    Completed,
    Skipped,
    Failed,
    Cancelled,
    /// <summary>Traducida: espera revisión antes de crear el .es.srt.</summary>
    PendingReview,
}

/// <summary>Fila de la lista de videos.</summary>
public sealed partial class VideoItemViewModel : ObservableObject
{
    private readonly List<string> _log = [];

    public VideoItemViewModel(string videoPath)
    {
        VideoPath = videoPath;
        Name = Path.GetFileName(videoPath);
        Folder = Path.GetDirectoryName(videoPath) ?? "";
    }

    public string VideoPath { get; }
    public string Name { get; }
    public string Folder { get; }

    [ObservableProperty] private VideoStatus? _status;
    [ObservableProperty] private RowState _state = RowState.Scanning;
    [ObservableProperty] private string _trackText = "analizando…";
    [ObservableProperty] private string _englishText = "…";
    [ObservableProperty] private string _spanishText = "…";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string? _logPath;
    [ObservableProperty] private VideoOutcome? _lastOutcome;

    public bool IsQueued => State == RowState.Queued;
    public bool IsBusy => State is RowState.Processing or RowState.Scanning;
    public bool HasFailed => State is RowState.Failed or RowState.Cancelled;

    /// <summary>Hay un borrador de traducción esperando revisión.</summary>
    public bool IsReviewPending => Status?.ReviewPending == true;

    /// <summary>Se puede abrir la revisión: borrador pendiente o .es.srt ya guardado.</summary>
    public bool CanReview => !IsBusy && (IsReviewPending
        || (EnglishPath is { } en && TranslationPaths.TryFromEnglish(en, out var p) && Status!.SpanishExisting.Contains(p!.SpanishPath, StringComparer.OrdinalIgnoreCase)));

    /// <summary>Ruta del glosario (si la pista elegida es SRT).</summary>
    public string? GlossaryPath =>
        Status?.EnglishPath is { } en && TranslationPaths.TryFromEnglish(en, out var p) ? p!.GlossaryPath : null;

    public string? EnglishPath => Status?.EnglishPath;

    public IReadOnlyList<string> LogLines => _log;

    partial void OnStateChanged(RowState value)
    {
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasFailed));
    }

    partial void OnStatusChanged(VideoStatus? value)
    {
        OnPropertyChanged(nameof(GlossaryPath));
        OnPropertyChanged(nameof(IsReviewPending));
        OnPropertyChanged(nameof(CanReview));
    }

    public void AddLog(string message) =>
        _log.Add($"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {message}");

    /// <summary>Muestra el resultado de un escaneo.</summary>
    public void ApplyStatus(VideoStatus st)
    {
        Status = st;
        TrackText = DescribeTrack(st);
        EnglishText = st.Error is not null || st.EnglishPath is null ? "❌" : st.EnglishExists ? "✅ existe" : "pendiente";
        if (State != RowState.Processing)
            SpanishText = st.SpanishExisting.Count > 0 ? "✅ existe" : st.ReviewPending ? ReviewText : State == RowState.Failed ? "❌" : "pendiente";
        if (State == RowState.Scanning)
        {
            State = RowState.Ready;
            var plan = VideoWorkflow.Plan(st, force: false);
            Detail = plan.Kind switch
            {
                VideoPlanKind.Process => plan.NeedsExtraction ? "listo: extraer y traducir" : "listo: traducir",
                VideoPlanKind.InspectError => st.Error ?? plan.Reason,
                VideoPlanKind.PendingReview => "traducción lista: doble clic para revisarla y guardarla",
                _ => plan.Reason,
            };
            if (st.Selection?.Kind == SubtitleKind.Forced && st.Selection.Track is not null)
                Detail += " · ⚠️ pista forzada: puede no cubrir todos los diálogos";
        }
    }

    public const string ReviewText = "📝 revisar";

    /// <summary>Tras cerrar la ventana de revisión.</summary>
    public void ApplyReviewOutcome(ReviewOutcome outcome)
    {
        switch (outcome)
        {
            case ReviewOutcome.Saved:
                State = RowState.Completed;
                SpanishText = "✅ existe";
                ProgressText = "revisada y guardada";
                Detail = "traducción revisada y guardada";
                AddLog("✅ Traducción revisada y guardada.");
                break;
            case ReviewOutcome.DraftSaved:
                SpanishText = ReviewText;
                Detail = "revisión a medias guardada: doble clic para seguir";
                AddLog("📝 Borrador de revisión guardado.");
                break;
            case ReviewOutcome.Discarded:
                State = RowState.Ready;
                SpanishText = "pendiente";
                ProgressText = "";
                ProgressValue = 0;
                Detail = "traducción descartada: vuelve a procesar para traducir";
                AddLog("🗑️ Traducción descartada.");
                break;
        }
    }

    public void SetScanError(string message)
    {
        State = RowState.Failed;
        TrackText = "—";
        EnglishText = "❌";
        SpanishText = "—";
        Detail = message;
        AddLog("Error al analizar: " + message);
    }

    public static string DescribeTrack(VideoStatus st)
    {
        if (st.Error is not null)
            return "error al inspeccionar";
        return st.Selection switch
        {
            { Track: { } t } sel => $"{t.Id} ({VideoWorkflow.KindLabel(sel.Kind)})",
            { Failure: SelectionFailure.ImageOnly } => "solo imagen — necesita OCR",
            _ => "sin inglés",
        };
    }

    public void MarkQueued()
    {
        State = RowState.Queued;
        ProgressValue = 0;
        IsProgressIndeterminate = false;
        ProgressText = "en cola";
    }

    public void Unqueue()
    {
        if (State != RowState.Queued)
            return;
        State = LastOutcome is null ? RowState.Ready : StateFor(LastOutcome.Status);
        ProgressText = "";
    }

    public void BeginProcessing()
    {
        State = RowState.Processing;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        ProgressText = "preparando…";
        Detail = "";
        AddLog("Inicio del procesamiento.");
    }

    public void ApplyProgress(VideoProgress p)
    {
        if (State != RowState.Processing)
            return;
        ProgressText = FormatProgress(p);
        if (p.Stage == VideoProgress.Translating)
            SpanishText = "en curso";
        if (p.Translation is { ChunkCount: > 0, BlocksTotal: > 0 } t)
        {
            IsProgressIndeterminate = false;
            ProgressValue = 100.0 * t.BlocksDone / t.BlocksTotal;
        }
        else
        {
            IsProgressIndeterminate = true;
        }
    }

    /// <summary>Texto de progreso: "tramo 2/5 · bloques 300/700", "extrayendo inglés…", "generando glosario…".</summary>
    public static string FormatProgress(VideoProgress p)
    {
        if (p.Stage == VideoProgress.Extracting)
            return "extrayendo inglés…";
        if (p.Translation is not { } t)
            return "traduciendo…";
        if (t.ChunkCount > 0)
            return $"tramo {t.ChunkNumber}/{t.ChunkCount} · bloques {t.BlocksDone}/{t.BlocksTotal}";
        return t.Stage == "glosario" ? "generando glosario…" : t.Stage + "…";
    }

    public void ApplyOutcome(VideoOutcome o)
    {
        LastOutcome = o;
        State = StateFor(o.Status);
        IsProgressIndeterminate = false;
        if (o.LogPath is not null)
            LogPath = o.LogPath;
        Detail = o.Detail;
        switch (o.Status)
        {
            case VideoOutcomeStatus.Completed:
                ProgressValue = 100;
                ProgressText = o.Translation is { } r ? $"completado · {r.BlockCount} bloques" : "completado";
                SpanishText = "✅ existe";
                break;
            case VideoOutcomeStatus.Skipped:
                ProgressText = "saltado";
                break;
            case VideoOutcomeStatus.PendingReview:
                ProgressValue = 100;
                ProgressText = "lista para revisar";
                SpanishText = ReviewText;
                Detail = o.Detail + " · doble clic para revisarla";
                break;
            case VideoOutcomeStatus.Cancelled:
                ProgressText = "cancelado (reanudable)";
                SpanishText = Status?.SpanishExisting.Count > 0 ? "✅ existe" : "pendiente";
                break;
            default:
                ProgressText = o.IsUsageLimit ? "en pausa (límite de uso)" : "error";
                SpanishText = Status?.SpanishExisting.Count > 0 ? "✅ existe" : "❌";
                break;
        }
        AddLog($"{SummaryRow.IconFor(o.Status)} {o.Detail}");
        foreach (var m in o.Messages)
            AddLog("   " + m);
        if (o.LogPath is not null)
            AddLog("   Log de traducción: " + o.LogPath);
    }

    private static RowState StateFor(VideoOutcomeStatus s) => s switch
    {
        VideoOutcomeStatus.Completed => RowState.Completed,
        VideoOutcomeStatus.Skipped => RowState.Skipped,
        VideoOutcomeStatus.Cancelled => RowState.Cancelled,
        VideoOutcomeStatus.PendingReview => RowState.PendingReview,
        _ => RowState.Failed,
    };

    public SummaryRow ToSummaryRow() => LastOutcome is { } o
        ? new SummaryRow(Name, SummaryRow.IconFor(o.Status), o.Detail)
        : new SummaryRow(Name, "⏭️", "no procesado");
}

/// <summary>Fila del resumen final.</summary>
public sealed record SummaryRow(string Name, string Icon, string Detail)
{
    public static string IconFor(VideoOutcomeStatus s) => s switch
    {
        VideoOutcomeStatus.Completed => "✅",
        VideoOutcomeStatus.Skipped => "⏭️",
        VideoOutcomeStatus.Cancelled => "⏸️",
        VideoOutcomeStatus.PendingReview => "📝",
        _ => "❌",
    };
}
