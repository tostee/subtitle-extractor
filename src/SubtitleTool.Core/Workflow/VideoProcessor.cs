using SubtitleTool.Core.Library;
using SubtitleTool.Core.Media;
using SubtitleTool.Core.Mkv;
using SubtitleTool.Core.Srt;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Workflow;

public enum VideoOutcomeStatus
{
    Completed,
    Skipped,
    Failed,
    /// <summary>Cancelado por el usuario; el progreso quedó guardado.</summary>
    Cancelled,
    /// <summary>Traducida; espera revisión antes de escribir el .es.srt.</summary>
    PendingReview,
}

/// <summary>Resultado de procesar un video (extraer si falta → traducir si falta).</summary>
public sealed record VideoOutcome
{
    public required VideoOutcomeStatus Status { get; init; }
    /// <summary>Motivo o detalle breve (tabla de resumen).</summary>
    public required string Detail { get; init; }
    public bool IsUsageLimit { get; init; }
    public bool IsAuthentication { get; init; }
    /// <summary>Falta configuración (p. ej. la clave de API): no tiene sentido seguir con más videos.</summary>
    public bool IsConfiguration { get; init; }
    public bool StopsBatch => IsUsageLimit || IsAuthentication || IsConfiguration;
    public bool Extracted { get; init; }
    public TranslationResult? Translation { get; init; }
    public string? LogPath { get; init; }
    public TokenUsage Usage { get; init; } = TokenUsage.Zero;
    public decimal CostUsd { get; init; }
    /// <summary>Advertencias y mensajes adicionales (para el log de la interfaz).</summary>
    public IReadOnlyList<string> Messages { get; init; } = [];
}

/// <summary>Fase actual de un video y, si se está traduciendo, su progreso.</summary>
public sealed record VideoProgress(string Stage, TranslationProgress? Translation = null)
{
    public const string Extracting = "extrayendo";
    public const string Translating = "traduciendo";
}

/// <summary>Ajustes de procesamiento de un video.</summary>
public sealed record VideoProcessingOptions
{
    public TranslationOptions Translation { get; init; } = new();
    public TimeSpan Timeout { get; init; } = ClaudeCodeTranslationClient.DefaultTimeout;
    public bool IsClaudeCode => Translation.Backend == TranslationBackends.ClaudeCode;
}

/// <summary>
/// Procesa un video: extrae el inglés si falta y traduce si no hay .es.srt. Nunca lanza por errores del video
/// (se devuelven como <see cref="VideoOutcomeStatus.Failed"/>); la cancelación se devuelve como <see cref="VideoOutcomeStatus.Cancelled"/>.
/// </summary>
public sealed class VideoProcessor
{
    private readonly Func<ISubtitleTrackExtractor> _extractorFactory;
    private readonly Func<VideoProcessingOptions, ITranslationClient?> _clientFactory;
    private readonly string? _jobsRoot;
    private readonly string _logsRoot;

    public VideoProcessor(Func<ISubtitleTrackExtractor> extractorFactory, Func<VideoProcessingOptions, ITranslationClient?> clientFactory,
        string? jobsRoot = null, string? logsRoot = null)
    {
        _extractorFactory = extractorFactory;
        _clientFactory = clientFactory;
        _jobsRoot = jobsRoot;
        _logsRoot = logsRoot ?? AppPaths.Logs;
    }

    public async Task<VideoOutcome> ProcessAsync(VideoStatus st, VideoProcessingOptions options, IProgress<VideoProgress>? progress = null,
        CancellationToken ct = default)
    {
        var plan = VideoWorkflow.Plan(st, options.Translation.Force);
        switch (plan.Kind)
        {
            case VideoPlanKind.Process:
                break;
            case VideoPlanKind.AlreadyTranslated:
                return new VideoOutcome { Status = VideoOutcomeStatus.Skipped, Detail = plan.Reason };
            case VideoPlanKind.PendingReview:
                return new VideoOutcome { Status = VideoOutcomeStatus.PendingReview, Detail = plan.Reason };
            case VideoPlanKind.InspectError:
                return new VideoOutcome { Status = VideoOutcomeStatus.Failed, Detail = $"{plan.Reason}: {st.Error}" };
            default:
                return new VideoOutcome { Status = VideoOutcomeStatus.Failed, Detail = plan.Reason };
        }

        var extracted = false;
        try
        {
            if (plan.NeedsExtraction)
            {
                progress?.Report(new VideoProgress(VideoProgress.Extracting));
                var ex = await _extractorFactory().ExtractAsync(st.VideoPath, st.Selection!.Track!.Id, st.EnglishPath!, ct).ConfigureAwait(false);
                if (ex.Status == ExtractionStatus.Failed)
                    return new VideoOutcome { Status = VideoOutcomeStatus.Failed, Detail = "extracción: " + ex.Message };
                extracted = ex.Status == ExtractionStatus.Extracted;
            }
        }
        catch (OperationCanceledException)
        {
            return new VideoOutcome { Status = VideoOutcomeStatus.Cancelled, Detail = "cancelado durante la extracción" };
        }
        catch (Exception ex) when (ex is MkvToolNixNotFoundException or FfmpegNotFoundException)
        {
            // Solo afecta a los videos de ese formato (.mkv o .mp4): la cola sigue con los demás.
            return new VideoOutcome { Status = VideoOutcomeStatus.Failed, Detail = ex.Message };
        }

        var client = _clientFactory(options);
        if (client is null)
        {
            return new VideoOutcome
            {
                Status = VideoOutcomeStatus.Failed, Detail = TranslationClientFactory.MissingApiKeyMessage,
                IsConfiguration = true, Extracted = extracted,
            };
        }

        var paths = TranslationPaths.FromEnglish(st.EnglishPath!);
        var log = new FileTranslationLog(_logsRoot, Path.GetFileNameWithoutExtension(paths.EnglishPath));
        var translator = new SubtitleTranslator(client, options.Translation, _jobsRoot);
        var translationProgress = progress is null
            ? null
            : new SyncProgress<TranslationProgress>(p => progress.Report(new VideoProgress(VideoProgress.Translating, p)));
        progress?.Report(new VideoProgress(VideoProgress.Translating));

        TranslationResult r;
        try
        {
            r = await translator.TranslateFileAsync(paths.EnglishPath, translationProgress, log, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new VideoOutcome
            {
                Status = VideoOutcomeStatus.Cancelled, Detail = "cancelado; el avance quedó guardado (reanudable)",
                Extracted = extracted, LogPath = log.Path,
            };
        }
        catch (TranslationClientException ex)
        {
            return new VideoOutcome
            {
                Status = VideoOutcomeStatus.Failed,
                Detail = ex.IsUsageLimit ? "límite de uso alcanzado (reanudable)" : ex.Message,
                IsUsageLimit = ex.IsUsageLimit, IsAuthentication = ex.IsAuthentication,
                Extracted = extracted, LogPath = log.Path,
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or SrtParseException or IOException or UnauthorizedAccessException)
        {
            return new VideoOutcome { Status = VideoOutcomeStatus.Failed, Detail = ex.Message, Extracted = extracted, LogPath = log.Path };
        }

        var messages = new List<string>(r.Warnings);
        if (r.Status == TranslationStatus.Completed && r.PlainCopy == PlainCopyStatus.SkippedExists)
            messages.Add($"Ya existe {Path.GetFileName(r.PlainCopyPath)} (sin idioma): no se tocó.");
        messages.AddRange(r.FailedBlocks.Take(20).Select(f => $"bloque {f.Id}: {f.Reason}"));
        messages.AddRange(r.Validation?.Errors.Take(20).Select(e => e.Message) ?? []);

        return new VideoOutcome
        {
            Status = r.Status switch
            {
                TranslationStatus.Completed => VideoOutcomeStatus.Completed,
                TranslationStatus.SkippedExists => VideoOutcomeStatus.Skipped,
                TranslationStatus.PendingReview => VideoOutcomeStatus.PendingReview,
                _ => VideoOutcomeStatus.Failed,
            },
            Detail = VideoWorkflow.ResultDetail(r, options.IsClaudeCode),
            Extracted = extracted,
            Translation = r,
            LogPath = r.LogPath ?? log.Path,
            Usage = r.Usage,
            CostUsd = r.EstimatedCostUsd,
            Messages = messages,
        };
    }

    /// <summary>IProgress que llama al delegado en el mismo hilo (el destino final ya marshalea al hilo de la interfaz).</summary>
    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
