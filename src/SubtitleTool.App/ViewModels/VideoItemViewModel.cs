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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnglishStatus))]
    private string _englishText = "…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpanishStatus))]
    private string _spanishText = "…";

    // Textos de las columnas Inglés / Español (sin emoji: el icono lo pone la vista según el tipo).
    public const string ExistsText = "existe";
    public const string ReviewText = "revisar";
    public const string PendingText = "pendiente";
    public const string InProgressText = "en curso";
    public const string ErrorText = "error";
    public const string UnavailableText = "no disponible";

    /// <summary>Tipo de estado (icono) de la columna Inglés.</summary>
    public StatusKind EnglishStatus => KindForCell(EnglishText);

    /// <summary>Tipo de estado (icono) de la columna Español.</summary>
    public StatusKind SpanishStatus => KindForCell(SpanishText);

    public static StatusKind KindForCell(string? text) => text switch
    {
        ExistsText => StatusKind.Success,
        ReviewText => StatusKind.Review,
        PendingText => StatusKind.Pending,
        InProgressText => StatusKind.InProgress,
        ErrorText or UnavailableText => StatusKind.Error,
        _ => StatusKind.None,
    };
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

    /// <summary>Se puede pedir la versión cubana: hay una pista en inglés .srt (extraída o por extraer).</summary>
    public bool CanTranslateCuban => !IsBusy && !IsQueued && Status is { } st && EnglishPath is { } en && TranslationPaths.TryFromEnglish(en, out _)
                                     && VideoWorkflow.Plan(st, force: true).Kind == VideoPlanKind.Process;

    /// <summary>Ruta de la versión cubana (<c>X.es-cu.srt</c>), si la pista en inglés es .srt.</summary>
    public string? CubanPath => EnglishPath is { } en && TranslationPaths.TryFromEnglish(en, out var p) ? p!.CubanPath : null;

    /// <summary>Estilo y sobrescritura con que se procesará la fila al salir de la cola.</summary>
    public TranslationStyle QueuedStyle { get; private set; } = TranslationStyle.Neutral;
    public bool QueuedForce { get; private set; }

    public IReadOnlyList<string> LogLines => _log;

    partial void OnStateChanged(RowState value)
    {
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(CanTranslateCuban));
    }

    partial void OnStatusChanged(VideoStatus? value)
    {
        OnPropertyChanged(nameof(GlossaryPath));
        OnPropertyChanged(nameof(IsReviewPending));
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(CanTranslateCuban));
        OnPropertyChanged(nameof(CubanPath));
    }

    /// <summary>Registro de la fila (texto plano: el tipo va en palabras, p. ej. «[error] …»).</summary>
    public void AddLog(string message, StatusKind kind = StatusKind.None) =>
        _log.Add($"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {StatusKinds.TextPrefix(kind)}{message}");

    /// <summary>Muestra el resultado de un escaneo.</summary>
    public void ApplyStatus(VideoStatus st)
    {
        Status = st;
        TrackText = DescribeTrack(st);
        EnglishText = st.Error is not null || st.EnglishPath is null ? UnavailableText : st.EnglishExists ? ExistsText : PendingText;
        if (State != RowState.Processing)
            SpanishText = st.SpanishExisting.Count > 0 ? ExistsText : st.ReviewPending ? ReviewText : State == RowState.Failed ? ErrorText : PendingText;
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
                Detail += " · atención: pista forzada, puede no cubrir todos los diálogos";
        }
    }

    /// <summary>Tras cerrar la ventana de revisión.</summary>
    public void ApplyReviewOutcome(ReviewOutcome outcome)
    {
        switch (outcome)
        {
            case ReviewOutcome.Saved:
                State = RowState.Completed;
                SpanishText = ExistsText;
                ProgressText = "revisada y guardada";
                Detail = "traducción revisada y guardada";
                AddLog("Traducción revisada y guardada.", StatusKind.Success);
                break;
            case ReviewOutcome.DraftSaved:
                SpanishText = ReviewText;
                Detail = "revisión a medias guardada: doble clic para seguir";
                AddLog("Borrador de revisión guardado.", StatusKind.Review);
                break;
            case ReviewOutcome.Discarded:
                State = RowState.Ready;
                SpanishText = PendingText;
                ProgressText = "";
                ProgressValue = 0;
                Detail = "traducción descartada: vuelve a procesar para traducir";
                AddLog("Traducción descartada.", StatusKind.Discarded);
                break;
        }
    }

    public void SetScanError(string message)
    {
        State = RowState.Failed;
        TrackText = "—";
        EnglishText = UnavailableText;
        SpanishText = "—";
        Detail = message;
        AddLog("Error al analizar: " + message, StatusKind.Error);
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

    public void MarkQueued(TranslationStyle style = TranslationStyle.Neutral, bool force = false)
    {
        QueuedStyle = style;
        QueuedForce = force;
        State = RowState.Queued;
        ProgressValue = 0;
        IsProgressIndeterminate = false;
        ProgressText = style == TranslationStyle.Cuban ? "en cola (versión cubana)" : "en cola";
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
        if (p.Stage == VideoProgress.Translating && QueuedStyle == TranslationStyle.Neutral)
            SpanishText = InProgressText;
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
        if (o.Style == TranslationStyle.Cuban)
        {
            // La versión cubana no cambia la columna Español (que es la traducción normal).
            if (Status is { } st)
                SpanishText = st.SpanishExisting.Count > 0 ? ExistsText : st.ReviewPending ? ReviewText : PendingText;
            if (o.Status == VideoOutcomeStatus.Completed)
                ProgressValue = 100;
            ProgressText = o.Status switch
            {
                VideoOutcomeStatus.Completed => "versión cubana lista",
                VideoOutcomeStatus.Skipped => "versión cubana ya existía",
                VideoOutcomeStatus.Cancelled => "versión cubana cancelada (reanudable)",
                _ => o.IsUsageLimit ? "en pausa (límite de uso)" : "versión cubana con error",
            };
            if (o.Status == VideoOutcomeStatus.Completed && o.Translation?.OutputPath is { } output)
                Detail = o.Detail + " · " + Path.GetFileName(output);
            AddLog(o.Detail, SummaryRow.KindFor(o.Status));
            foreach (var m in o.Messages)
                AddLog("   " + m);
            if (o.LogPath is not null)
                AddLog("   Registro de traducción: " + o.LogPath);
            return;
        }
        switch (o.Status)
        {
            case VideoOutcomeStatus.Completed:
                ProgressValue = 100;
                ProgressText = o.Translation is { } r ? $"completado · {r.BlockCount} bloques" : "completado";
                SpanishText = ExistsText;
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
                SpanishText = Status?.SpanishExisting.Count > 0 ? ExistsText : PendingText;
                break;
            default:
                ProgressText = o.IsUsageLimit ? "en pausa (límite de uso)" : "error";
                SpanishText = Status?.SpanishExisting.Count > 0 ? ExistsText : ErrorText;
                break;
        }
        AddLog(o.Detail, SummaryRow.KindFor(o.Status));
        foreach (var m in o.Messages)
            AddLog("   " + m);
        if (o.LogPath is not null)
            AddLog("   Registro de traducción: " + o.LogPath);
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
        ? new SummaryRow(Name, SummaryRow.KindFor(o.Status), o.Detail)
        : new SummaryRow(Name, StatusKind.Skipped, "no procesado");
}

/// <summary>Fila del resumen final: el tipo (icono y palabra) se pinta en la vista.</summary>
public sealed record SummaryRow(string Name, StatusKind Kind, string Detail)
{
    public static StatusKind KindFor(VideoOutcomeStatus s) => s switch
    {
        VideoOutcomeStatus.Completed => StatusKind.Success,
        VideoOutcomeStatus.Skipped => StatusKind.Skipped,
        VideoOutcomeStatus.Cancelled => StatusKind.Cancelled,
        VideoOutcomeStatus.PendingReview => StatusKind.Review,
        _ => StatusKind.Error,
    };

    /// <summary>Estado en palabras (la columna «Estado» no depende solo del icono).</summary>
    public string StatusLabel => Kind == StatusKind.Error ? "con error" : StatusKinds.Label(Kind);
}
