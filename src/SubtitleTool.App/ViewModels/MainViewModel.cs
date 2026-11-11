using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubtitleTool.App.Models;
using SubtitleTool.App.Services;
using SubtitleTool.Core.Media;
using SubtitleTool.Core.Review;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Updates;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.App.ViewModels;

/// <summary>Ventana principal: lista de videos, cola de procesamiento secuencial y barra de estado.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const string UsageLimitMessage =
        "Se alcanzó el límite de tu plan. El avance quedó guardado; vuelve a pulsar Procesar más tarde.";

    private readonly ISettingsStore _settingsStore;
    private readonly ILibraryService _library;
    private readonly IVideoProcessingService _processing;
    private readonly IGlossaryService _glossary;
    private readonly IDoctorService _doctor;
    private readonly IApiKeyService _apiKeys;
    private readonly IDialogService _dialogs;
    private readonly IActivityLog _activityLog;
    private readonly IUpdateService _updates;
    private readonly IReviewService _reviews;
    private bool _checkingUpdates;

    private AppSettings _settings;
    private CancellationTokenSource? _cts;
    private int _scanning;
    private bool _mkvToolNixWarningShown;
    // Mientras la ventana de revisión está abierta, los avisos de fin de cola esperan a que se cierre.
    private bool _reviewOpen;
    private readonly List<Action> _deferredDialogs = [];
    private TokenUsage _sessionUsage = TokenUsage.Zero;
    private decimal _sessionCost;
    private TokenUsage _currentUsage = TokenUsage.Zero;
    private decimal _currentCost;
    private int _queueDone;
    private int _queueTotal;

    public MainViewModel(ISettingsStore settingsStore, ILibraryService library, IVideoProcessingService processing,
        IGlossaryService glossary, IDoctorService doctor, IApiKeyService apiKeys, IDialogService dialogs,
        IActivityLog? activityLog = null, IUpdateService? updates = null, IReviewService? reviews = null)
    {
        _reviews = reviews ?? new ReviewServiceAdapter();
        _activityLog = activityLog ?? NullActivityLog.Instance;
        _updates = updates ?? NullUpdateService.Instance;
        _settingsStore = settingsStore;
        _library = library;
        _processing = processing;
        _glossary = glossary;
        _doctor = doctor;
        _apiKeys = apiKeys;
        _dialogs = dialogs;
        _settings = settingsStore.Load().Normalize();
        _selectedBackend = _settings.Backend;
        _selectedModel = _settings.Model;
        UpdateUsageText();
    }

    public ObservableCollection<VideoItemViewModel> Videos { get; } = [];
    /// <summary>Registro de la ventana: cada línea con su tipo (la vista pinta el icono) y el texto sin emoji.</summary>
    public ObservableCollection<LogEntry> LogLines { get; } = [];

    public IReadOnlyList<Choice> Backends => Choices.Backends;
    public IReadOnlyList<Choice> Models => Choices.Models;

    /// <summary>Copia de los ajustes actuales.</summary>
    public AppSettings Settings => _settings.Clone();

    [ObservableProperty] private VideoItemViewModel? _selectedVideo;
    [ObservableProperty] private string _selectedBackend;
    [ObservableProperty] private string _selectedModel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ProcessAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearListCommand))]
    [NotifyCanExecuteChangedFor(nameof(ProcessOneCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranslateCubanCommand))]
    private bool _isProcessing;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _currentVideoText = "Sin actividad";
    [ObservableProperty] private double _globalProgress;
    [ObservableProperty] private string _globalProgressText = "";
    [ObservableProperty] private string _usageText = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ProcessOneCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranslateCubanCommand))]
    private bool _isCancelling;

    public bool HasVideos => Videos.Count > 0;

    partial void OnSelectedBackendChanged(string value)
    {
        if (_settings.Backend == value || Choices.Backends.All(c => c.Value != value))
            return;
        _settings.Backend = value;
        SaveSettings();
        UpdateUsageText();
    }

    partial void OnSelectedModelChanged(string value)
    {
        if (_settings.Model == value || !ModelPricing.Known.ContainsKey(value))
            return;
        _settings.Model = value;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"No se pudieron guardar los ajustes: {ex.Message}");
        }
    }

    public void Log(string message, StatusKind kind = StatusKind.None)
    {
        var line = $"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {message}";
        LogLines.Add(new LogEntry(kind, line));
        while (LogLines.Count > 2000)
            LogLines.RemoveAt(0);
        _activityLog.Write(StatusKinds.TextPrefix(kind) + message);
    }

    /// <summary>Error inesperado: mensaje breve en el panel y traza completa en el archivo de registro.</summary>
    public void LogException(string context, Exception exception)
    {
        Log($"{context}: {exception.Message}", StatusKind.Error);
        _activityLog.WriteException(context, exception);
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(_activityLog.FolderPath);
        _dialogs.OpenFolder(_activityLog.FolderPath);
    }

    /// <summary>Al iniciar: avisa en el registro si faltan MKVToolNix (para .mkv) o FFmpeg (para .mp4).</summary>
    public void ReportToolStatus()
    {
        var mkv = _library.DetectMkvToolNix(_settings.MkvToolNixDirectory);
        if (mkv is null)
            Log("No se encontró MKVToolNix: hace falta para los .mkv (https://mkvtoolnix.download/ o Ajustes → MKVToolNix).", StatusKind.Warning);
        else
            Log($"MKVToolNix: {mkv}");
        var ffmpeg = _library.DetectFfmpeg(_settings.FfmpegDirectory);
        if (ffmpeg is null)
            Log($"No se encontró FFmpeg: solo hace falta para los .mp4 (winget install {FfmpegLocator.WingetPackage} o Ajustes → FFmpeg).", StatusKind.Info);
        else
            Log($"FFmpeg: {ffmpeg}");
    }

    // ------------------------------------------------------------------ revisión

    /// <summary>Abre la ventana «Revisar traducción» (borrador pendiente o .es.srt ya guardado).</summary>
    [RelayCommand]
    private async Task ReviewAsync(VideoItemViewModel? row)
    {
        if (row is null)
            return;
        if (row.IsBusy || row.IsQueued)
        {
            _dialogs.ShowInfo("Este video se está procesando; espera a que termine para revisarlo.");
            return;
        }
        if (row.EnglishPath is not { } english || !english.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.ShowInfo("Este video no tiene una pista en inglés .srt: no hay traducción que revisar.");
            return;
        }

        ReviewSession session;
        try
        {
            session = await _reviews.OpenAsync(english);
        }
        catch (ReviewException ex) when (ex.IsStaleDraft)
        {
            // El borrador ya no encaja con el inglés: la única salida es descartarlo (y volver a traducir).
            if (_dialogs.Confirm(ex.Message + "\n\n¿Descartar el borrador ahora?", "Revisar traducción"))
            {
                _reviews.Discard(english);
                Log($"{row.Name}: borrador de revisión descartado (el inglés había cambiado).", StatusKind.Discarded);
                row.ApplyReviewOutcome(ReviewOutcome.Discarded);
                await RescanAfterReviewAsync(row);
            }
            return;
        }
        catch (ReviewException ex)
        {
            _dialogs.ShowInfo(ex.Message, "Revisar traducción");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SubtitleTool.Core.Srt.SrtParseException or ArgumentException)
        {
            LogException($"No se pudo abrir la revisión de {row.Name}", ex);
            _dialogs.ShowError("No se pudo abrir la revisión: " + ex.Message);
            return;
        }

        var vm = new ReviewViewModel(session, _reviews, _dialogs);
        _reviewOpen = true;
        try
        {
            _dialogs.ShowReview(vm);
        }
        finally
        {
            _reviewOpen = false;
        }
        ShowDeferredDialogs();
        if (vm.Outcome == ReviewOutcome.None)
            return;

        row.ApplyReviewOutcome(vm.Outcome);
        var (message, kind) = vm.Outcome switch
        {
            ReviewOutcome.Saved => ($"{row.Name}: traducción revisada y guardada ({session.EditedIds.Count()} bloque(s) editados).", StatusKind.Success),
            ReviewOutcome.DraftSaved => ($"{row.Name}: revisión guardada como borrador.", StatusKind.Review),
            _ => ($"{row.Name}: traducción descartada.", StatusKind.Discarded),
        };
        Log(message, kind);
        await RescanAfterReviewAsync(row);
    }

    /// <summary>
    /// Avisos que aparecen solos (fin de cola): si la revisión está abierta se guardan y se muestran al cerrarla,
    /// para no tapar ni interrumpir lo que el usuario está escribiendo.
    /// </summary>
    private void ShowWhenFree(Action show)
    {
        if (!_reviewOpen)
        {
            show();
            return;
        }
        if (_deferredDialogs.Count == 0)
            Log("La cola terminó mientras revisabas: el aviso se mostrará al cerrar la revisión.", StatusKind.Info);
        _deferredDialogs.Add(show);
    }

    private void ShowDeferredDialogs()
    {
        var pending = _deferredDialogs.ToList();
        _deferredDialogs.Clear();
        foreach (var show in pending)
            show();
    }

    private async Task RescanAfterReviewAsync(VideoItemViewModel row)
    {
        try
        {
            row.ApplyStatus(await _library.ScanAsync(row.VideoPath, _settings.Tools, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            row.AddLog("No se pudo actualizar el estado: " + ex.Message);
        }
    }

    /// <summary>Doble clic en una fila: revisar si está pendiente; si no, ver su log.</summary>
    [RelayCommand]
    private Task OpenRowAsync(VideoItemViewModel? row)
    {
        if (row is null)
            return Task.CompletedTask;
        if (row.IsReviewPending && !row.IsBusy)
            return ReviewAsync(row);
        ViewLog(row);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ actualizaciones

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(UpdateBannerText))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private ReleaseInfo? _availableUpdate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private bool _isUpdating;

    [ObservableProperty] private string _updateStatusText = "";

    public bool HasUpdate => AvailableUpdate is not null;

    public string VersionText => "v" + _updates.CurrentVersion;

    public string UpdateBannerText => AvailableUpdate is { } r
        ? $"Hay una versión nueva de Subtítulos: {r.Version.ToString(3)} (tienes la {_updates.CurrentVersion})."
        : "";

    public string UpdateButtonText => _updates.CanSelfUpdate ? "Actualizar ahora" : "Descargar";

    /// <summary>Al abrir la app (si está activado en Ajustes). Sin conexión no molesta: solo lo anota en el registro.</summary>
    public Task CheckForUpdatesOnStartupAsync() => _settings.CheckForUpdates ? CheckForUpdatesAsync(manual: false) : Task.CompletedTask;

    [RelayCommand]
    private Task CheckUpdatesAsync() => CheckForUpdatesAsync(manual: true);

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingUpdates)
            return;
        _checkingUpdates = true;
        try
        {
            var newer = await _updates.FindNewerAsync();
            if (newer is null)
            {
                if (manual)
                    _dialogs.ShowInfo($"Ya tienes la versión más reciente ({_updates.CurrentVersion}).", "Actualizaciones");
                return;
            }
            AvailableUpdate = newer;
            Log($"Hay una versión nueva: {newer.Version.ToString(3)} — {newer.PageUrl}", StatusKind.Update);
        }
        catch (UpdateException ex)
        {
            Log("No se pudo buscar actualizaciones: " + ex.Message, StatusKind.Warning);
            if (manual)
                _dialogs.ShowWarning("No se pudo buscar actualizaciones: " + ex.Message, "Actualizaciones");
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { } release)
            return;
        if (!_updates.CanSelfUpdate)
        {
            _dialogs.OpenUrl(release.PageUrl);
            return;
        }
        if (IsProcessing)
        {
            _dialogs.ShowWarning("Hay videos procesándose. Cancela o espera a que terminen antes de actualizar (el avance se guarda).", "Actualizaciones");
            return;
        }
        if (!_dialogs.Confirm($"Se descargará e instalará la versión {release.Version.ToString(3)}.\n" +
                              "Windows pedirá permiso de administrador (se instala en Program Files). La aplicación se cerrará " +
                              "y se volverá a abrir sola. Tus ajustes y el avance se conservan.\n\n¿Continuar?", "Actualizar"))
            return;

        IsUpdating = true;
        UpdateStatusText = "Descargando… 0 %";
        try
        {
            var progress = new Progress<double>(p => UpdateStatusText = $"Descargando… {(int)(p * 100)} %");
            var installer = await _updates.DownloadAsync(release, progress);
            UpdateStatusText = "Instalando…";
            Log($"Actualizando a la versión {release.Version.ToString(3)} (instalador verificado: {Path.GetFileName(installer)}).");
            _updates.InstallAndExit(installer);
        }
        catch (UpdateException ex)
        {
            Log("No se pudo actualizar: " + ex.Message, StatusKind.Error);
            _dialogs.ShowError(ex.Message, "No se pudo actualizar");
        }
        finally
        {
            IsUpdating = false;
            UpdateStatusText = "";
        }
    }

    private bool CanInstallUpdate() => AvailableUpdate is not null && !IsUpdating;

    [RelayCommand]
    private void ViewReleaseNotes()
    {
        if (AvailableUpdate is not { } r)
            return;
        var notes = string.IsNullOrWhiteSpace(r.Notes) ? "(sin notas)" : r.Notes.Trim();
        _dialogs.ShowText($"Novedades de la versión {r.Version.ToString(3)}", notes + Environment.NewLine + Environment.NewLine + r.PageUrl, prose: true);
    }

    [RelayCommand]
    private void DismissUpdate() => AvailableUpdate = null;

    // ------------------------------------------------------------------ agregar videos

    [RelayCommand]
    private Task AddFolderAsync()
    {
        var folder = _dialogs.PickFolder("Elige una carpeta con videos (.mkv, .mp4)");
        return folder is null ? Task.CompletedTask : AddPathsAsync([folder]);
    }

    [RelayCommand]
    private Task AddFilesAsync()
    {
        var files = _dialogs.PickVideoFiles();
        return files.Count == 0 ? Task.CompletedTask : AddPathsAsync(files);
    }

    /// <summary>Agrega carpetas o videos (botones, arrastrar y soltar, línea de comandos) y los analiza en segundo plano.</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var added = new List<VideoItemViewModel>();
        foreach (var path in paths)
        {
            IReadOnlyList<string> found;
            try
            {
                found = await Task.Run(() => _library.FindVideos(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Log($"No se pudo leer '{path}': {ex.Message}", StatusKind.Error);
                continue;
            }
            if (found.Count == 0)
            {
                Log($"No se encontraron videos ({VideoFormats.Description}) en: {path}", StatusKind.Warning);
                continue;
            }
            Log($"Agregados {found.Count} video(s) de: {path}");
            foreach (var video in found)
            {
                if (Videos.Any(v => string.Equals(v.VideoPath, video, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var row = new VideoItemViewModel(video);
                row.PropertyChanged += OnRowPropertyChanged;
                Videos.Add(row);
                added.Add(row);
            }
        }
        OnPropertyChanged(nameof(HasVideos));
        if (added.Count == 0)
            return;

        _scanning++;
        IsScanning = true;
        try
        {
            foreach (var row in added)
                await ScanRowAsync(row);
        }
        finally
        {
            IsScanning = --_scanning > 0;
        }
        ProcessAllCommand.NotifyCanExecuteChanged();
    }

    private async Task<bool> ScanRowAsync(VideoItemViewModel row)
    {
        try
        {
            var status = await _library.ScanAsync(row.VideoPath, _settings.Tools);
            row.ApplyStatus(status);
            if (status.Error is not null)
                Log($"{row.Name}: {status.Error}", StatusKind.Error);
            if (status.MkvToolNixMissing)
                WarnMkvToolNixMissing(status.Error!);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            row.SetScanError(ex.Message);
            LogException(row.Name, ex);
        }
        return false;
    }

    /// <summary>Sin MKVToolNix solo fallan los .mkv (los .mp4 siguen); se avisa una vez hasta que cambien los Ajustes.</summary>
    private void WarnMkvToolNixMissing(string message)
    {
        if (_mkvToolNixWarningShown)
            return;
        _mkvToolNixWarningShown = true;
        _dialogs.ShowError(message + "\n\nLos .mkv no se pueden procesar hasta que lo instales o indiques la carpeta en "
                           + "Ajustes → MKVToolNix. Los .mp4 se procesan igualmente.", "MKVToolNix no encontrado");
    }

    /// <summary>Al cambiar el estado de una fila, se recalcula qué acciones de fila están disponibles (menú contextual).</summary>
    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoItemViewModel.State))
        {
            ProcessOneCommand.NotifyCanExecuteChanged();
            RetryCommand.NotifyCanExecuteChanged();
            TranslateCubanCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearList))]
    private void ClearList()
    {
        Videos.Clear();
        OnPropertyChanged(nameof(HasVideos));
        ProcessAllCommand.NotifyCanExecuteChanged();
    }

    private bool CanClearList() => !IsProcessing;

    // ------------------------------------------------------------------ procesamiento

    [RelayCommand(CanExecute = nameof(CanProcessAll))]
    private Task ProcessAllAsync()
    {
        var any = false;
        foreach (var row in Videos.Where(v => !v.IsBusy))
        {
            row.MarkQueued();
            any = true;
        }
        return any ? RunQueueAsync(showSummary: true) : Task.CompletedTask;
    }

    private bool CanProcessAll() => !IsProcessing;

    /// <summary>Procesar solo este video (si la cola ya está en marcha, se añade al final).</summary>
    // AllowConcurrentExecutions: mientras la cola que empezó este comando sigue en marcha, se pueden añadir más videos.
    [RelayCommand(CanExecute = nameof(CanProcessOne), AllowConcurrentExecutions = true)]
    private Task ProcessOneAsync(VideoItemViewModel? row)
    {
        if (row is null || row.IsBusy || row.IsQueued)
            return Task.CompletedTask;
        row.MarkQueued();
        return IsProcessing ? Task.CompletedTask : RunQueueAsync(showSummary: false);
    }

    /// <summary>Mientras se cancela la cola no se añaden videos (se descartarían al terminar la cancelación).</summary>
    private bool CanProcessOne(VideoItemViewModel? row) => !IsCancelling && row is { IsBusy: false, IsQueued: false };

    [RelayCommand(CanExecute = nameof(CanRetry), AllowConcurrentExecutions = true)]
    private Task RetryAsync(VideoItemViewModel? row) => ProcessOneAsync(row);

    private bool CanRetry(VideoItemViewModel? row) => CanProcessOne(row) && row!.HasFailed;

    /// <summary>
    /// Versión en cubano callejero (para reírse): va a <c>X.es-cu.srt</c>, aparte de la traducción normal, y se añade a la
    /// cola como «Procesar solo este». Si ya existe, pregunta antes de rehacerla.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTranslateCuban), AllowConcurrentExecutions = true)]
    private Task TranslateCubanAsync(VideoItemViewModel? row)
    {
        if (row is null || !row.CanTranslateCuban)
            return Task.CompletedTask;
        var force = false;
        if (row.CubanPath is { } cuban && File.Exists(cuban))
        {
            if (!_dialogs.Confirm($"Ya existe la versión cubana:\n{Path.GetFileName(cuban)}\n\n¿Hacerla de nuevo y reemplazarla?", "Versión cubana"))
                return Task.CompletedTask;
            force = true;
        }
        row.MarkQueued(TranslationStyle.Cuban, force);
        Log($"{row.Name}: en cola la versión cubana (se guardará como {Path.GetFileName(row.CubanPath)}).");
        return IsProcessing ? Task.CompletedTask : RunQueueAsync(showSummary: false);
    }

    private bool CanTranslateCuban(VideoItemViewModel? row) => !IsCancelling && row is { CanTranslateCuban: true };

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_cts is null || _cts.IsCancellationRequested)
            return;
        IsCancelling = true;
        CurrentVideoText = "Cancelando… (el avance se guarda)";
        Log("Cancelación solicitada.");
        _cts.Cancel();
    }

    private bool CanCancel() => IsProcessing && !IsCancelling;

    /// <summary>Procesa la cola de uno en uno (para no agotar el límite de la suscripción).</summary>
    private async Task RunQueueAsync(bool showSummary)
    {
        if (IsProcessing)
            return;
        IsProcessing = true;
        IsCancelling = false;
        _cts = new CancellationTokenSource();
        var processed = new List<VideoItemViewModel>();
        VideoOutcome? stopOutcome = null;
        _queueDone = 0;
        _queueTotal = Videos.Count(v => v.IsQueued);
        UpdateGlobalProgress(0);
        Log($"Procesando {_queueTotal} video(s) — modo {Choices.Label(Choices.Backends, _settings.Backend)}, modelo {Choices.Label(Choices.Models, _settings.Model)}.");

        try
        {
            while (!_cts.IsCancellationRequested && Videos.FirstOrDefault(v => v.IsQueued) is { } row)
            {
                processed.Add(row);
                var outcome = await ProcessRowAsync(row, _cts.Token);
                _queueDone++;
                _queueTotal = _queueDone + Videos.Count(v => v.IsQueued);
                UpdateGlobalProgress(0);
                if (outcome.StopsBatch)
                {
                    stopOutcome = outcome;
                    break;
                }
            }
        }
        finally
        {
            foreach (var row in Videos.Where(v => v.IsQueued))
                row.Unqueue();
            var cancelled = _cts.IsCancellationRequested;
            _cts.Dispose();
            _cts = null;
            IsProcessing = false;
            IsCancelling = false;
            CurrentVideoText = cancelled ? "Cancelado: el avance quedó guardado." : "Sin actividad";
        }

        if (stopOutcome is not null)
        {
            if (stopOutcome.IsUsageLimit)
            {
                Log(UsageLimitMessage, StatusKind.Paused);
                ShowWhenFree(() => _dialogs.ShowWarning(UsageLimitMessage, "Límite de uso alcanzado"));
            }
            else
            {
                Log("Se detuvo la cola: " + stopOutcome.Detail, StatusKind.Paused);
                var detail = stopOutcome.Detail;
                ShowWhenFree(() => _dialogs.ShowError(detail + "\n\nSe detuvo la cola; el avance quedó guardado.", "No se puede continuar"));
            }
        }

        var toReview = processed.Count(r => r.LastOutcome?.Status == VideoOutcomeStatus.PendingReview);
        if (toReview > 0)
            Log($"{toReview} traducción(es) lista(s) para revisar: doble clic o Enter en la fila (o clic derecho → «Revisar traducción…»).", StatusKind.Review);

        if (showSummary && processed.Count > 0)
        {
            var summary = processed.Select(r => r.ToSummaryRow()).ToList();
            ShowWhenFree(() => _dialogs.ShowSummary(summary));
        }
    }

    private async Task<VideoOutcome> ProcessRowAsync(VideoItemViewModel row, CancellationToken ct)
    {
        CurrentVideoText = row.Name;
        row.BeginProcessing();
        _currentUsage = TokenUsage.Zero;
        _currentCost = 0;
        VideoOutcome outcome;
        try
        {
            // Se vuelve a analizar justo antes: los archivos pueden haber cambiado desde que se agregó.
            var status = await _library.ScanAsync(row.VideoPath, _settings.Tools, ct);
            row.ApplyStatus(status);
            var progress = new Progress<VideoProgress>(p => OnProgress(row, p));
            var settings = _settings.Clone();
            settings.Style = row.QueuedStyle;
            settings.Force = row.QueuedForce;
            outcome = await _processing.ProcessAsync(status, settings, progress, ct);
        }
        catch (OperationCanceledException)
        {
            outcome = new VideoOutcome { Status = VideoOutcomeStatus.Cancelled, Detail = "cancelado; el avance quedó guardado (reanudable)" };
        }
        catch (Exception ex)
        {
            // Nunca se cierra la app por el error de un video.
            outcome = new VideoOutcome { Status = VideoOutcomeStatus.Failed, Detail = $"error inesperado: {ex.Message}" };
            row.AddLog(ex.ToString());
            _activityLog.WriteException(row.Name, ex);
        }

        _sessionUsage = _sessionUsage.Add(outcome.Usage);
        _sessionCost += outcome.CostUsd;
        _currentUsage = TokenUsage.Zero;
        _currentCost = 0;
        UpdateUsageText();

        try
        {
            row.ApplyStatus(await _library.ScanAsync(row.VideoPath, _settings.Tools, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            row.AddLog("No se pudo actualizar el estado: " + ex.Message);
        }
        row.ApplyOutcome(outcome);
        Log($"{row.Name}: {outcome.Detail}", SummaryRow.KindFor(outcome.Status));
        foreach (var m in outcome.Messages.Take(10))
            Log("   " + m);
        return outcome;
    }

    private void OnProgress(VideoItemViewModel row, VideoProgress p)
    {
        row.ApplyProgress(p);
        if (p.Translation is { } t)
        {
            _currentUsage = t.Usage;
            _currentCost = t.EstimatedCostUsd;
            UpdateUsageText();
            UpdateGlobalProgress(t.BlocksTotal > 0 && t.ChunkCount > 0 ? (double)t.BlocksDone / t.BlocksTotal : 0);
        }
    }

    private void UpdateGlobalProgress(double currentFraction)
    {
        if (_queueTotal <= 0)
        {
            GlobalProgress = 0;
            GlobalProgressText = "";
            return;
        }
        GlobalProgress = 100.0 * (_queueDone + Math.Clamp(currentFraction, 0, 1)) / _queueTotal;
        GlobalProgressText = $"{Math.Min(_queueDone + (IsProcessing ? 1 : 0), _queueTotal)}/{_queueTotal} videos";
    }

    private void UpdateUsageText()
    {
        var usage = _sessionUsage.Add(_currentUsage);
        var cost = _sessionCost + _currentCost;
        var input = usage.InputTokens + usage.CacheReadInputTokens + usage.CacheCreationInputTokens;
        UsageText = $"Tokens: {input.ToString("N0", CultureInfo.CurrentCulture)} ent / {usage.OutputTokens.ToString("N0", CultureInfo.CurrentCulture)} sal · " +
                    VideoWorkflow.FormatCost(cost, _settings.IsClaudeCode);
    }

    // ------------------------------------------------------------------ acciones de fila

    [RelayCommand]
    private void OpenFolder(VideoItemViewModel? row)
    {
        if (row is not null)
            _dialogs.OpenFolder(row.VideoPath);
    }

    [RelayCommand]
    private void OpenGlossary(VideoItemViewModel? row)
    {
        if (row is null)
            return;
        if (row.GlossaryPath is null || row.EnglishPath is null)
        {
            _dialogs.ShowInfo("Este video no tiene una pista en inglés .srt, así que no tiene glosario.");
            return;
        }
        var vm = new GlossaryEditorViewModel(row.GlossaryPath, row.EnglishPath, _glossary, _dialogs, () => _settings.Clone());
        if (!vm.Load())
            return;
        _dialogs.ShowGlossaryEditor(vm);
    }

    [RelayCommand]
    private void ViewLog(VideoItemViewModel? row)
    {
        if (row is null)
            return;
        var text = row.LogLines.Count == 0 ? "(sin mensajes todavía)" : string.Join(Environment.NewLine, row.LogLines);
        if (row.LogPath is not null)
            text += Environment.NewLine + Environment.NewLine + "Registro detallado de la traducción: " + row.LogPath;
        _dialogs.ShowText($"Registro — {row.Name}", text);
    }

    [RelayCommand]
    private void OpenTranslationLog(VideoItemViewModel? row)
    {
        if (row?.LogPath is { } path && File.Exists(path))
            _dialogs.OpenFile(path);
        else
            _dialogs.ShowInfo("Este video todavía no tiene registro de traducción.");
    }

    // ------------------------------------------------------------------ ajustes

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var vm = new SettingsViewModel(_settings.Clone(), _library, _doctor, _apiKeys, _dialogs);
        if (!_dialogs.ShowSettings(vm))
            return;
        var oldTools = _settings.Tools;
        _settings = vm.Result.Normalize();
        SaveSettings();
        SelectedBackend = _settings.Backend;
        SelectedModel = _settings.Model;
        UpdateUsageText();
        Log("Ajustes guardados.", StatusKind.Success);

        // Si cambió la ruta de MKVToolNix o de FFmpeg, se vuelven a analizar los videos que fallaron al analizarse.
        var newTools = _settings.Tools;
        if (!string.Equals(oldTools.MkvToolNix, newTools.MkvToolNix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(oldTools.Ffmpeg, newTools.Ffmpeg, StringComparison.OrdinalIgnoreCase))
        {
            _mkvToolNixWarningShown = false;
            var toRescan = Videos.Where(v => !v.IsBusy && !v.IsQueued
                                             && ((v.Status is null && v.State == RowState.Failed) || v.Status?.Error is not null)).ToList();
            foreach (var row in toRescan)
            {
                row.State = RowState.Scanning;
                await ScanRowAsync(row);
            }
        }
    }
}
