using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubtitleTool.App.Models;
using SubtitleTool.App.Services;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.App.ViewModels;

/// <summary>Ventana de ajustes. La clave de API se guarda/borra al instante (DPAPI); el resto al pulsar Guardar.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ILibraryService _library;
    private readonly IDoctorService _doctor;
    private readonly IApiKeyService _apiKeys;
    private readonly IDialogService _dialogs;

    public SettingsViewModel(AppSettings settings, ILibraryService library, IDoctorService doctor, IApiKeyService apiKeys, IDialogService dialogs)
    {
        _library = library;
        _doctor = doctor;
        _apiKeys = apiKeys;
        _dialogs = dialogs;
        Result = settings.Clone();
        _backend = settings.Backend;
        _model = settings.Model;
        _effort = settings.Effort;
        _chunkSizeText = settings.ChunkSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _timeoutText = settings.TimeoutMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _createPlainCopy = settings.CreatePlainCopy;
        _checkForUpdates = settings.CheckForUpdates;
        _reviewBeforeSave = settings.ReviewBeforeSave;
        _mkvToolNixDirectory = settings.MkvToolNixDirectory ?? "";
        _ffmpegDirectory = settings.FfmpegDirectory ?? "";
        RefreshDetectedMkvToolNix();
        RefreshDetectedFfmpeg();
        RefreshApiKeyStatus();
    }

    public IReadOnlyList<Choice> Backends => Choices.Backends;
    public IReadOnlyList<Choice> Models => Choices.Models;
    public IReadOnlyList<Choice> Efforts => Choices.Efforts;

    /// <summary>Ajustes resultantes (válidos tras <see cref="TrySave"/>).</summary>
    public AppSettings Result { get; private set; }

    [ObservableProperty] private string _backend;
    [ObservableProperty] private string _model;
    [ObservableProperty] private string _effort;
    [ObservableProperty] private string _chunkSizeText;
    [ObservableProperty] private string _timeoutText;
    [ObservableProperty] private bool _createPlainCopy;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private bool _reviewBeforeSave;
    [ObservableProperty] private string _mkvToolNixDirectory;
    [ObservableProperty] private string _ffmpegDirectory;
    [ObservableProperty] private string _errorText = "";

    /// <summary>Campo al que se refiere <see cref="ErrorText"/> (la vista le lleva el foco), o null.</summary>
    [ObservableProperty] private SettingsField? _errorField;
    [ObservableProperty] private string _apiKeyStatus = "";
    [ObservableProperty] private bool _canClearApiKey;
    [ObservableProperty] private string _checkStatus = "";

    /// <summary>Tipo del resumen de la comprobación (icono).</summary>
    [ObservableProperty] private StatusKind _checkStatusKind;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private bool _isChecking;

    [ObservableProperty] private string _detectedMkvToolNixText = "";
    [ObservableProperty] private string _detectedFfmpegText = "";

    private CancellationTokenSource? _checkCts;

    // Al escribir la ruta, la detección (E/S de disco, quizá rutas de red) se hace en segundo plano y con espera:
    // solo cuenta el último valor. «Examinar» y «Autodetectar» la hacen al momento.
    partial void OnMkvToolNixDirectoryChanged(string value) => ScheduleDetection(mkv: true);
    partial void OnFfmpegDirectoryChanged(string value) => ScheduleDetection(mkv: false);

    /// <summary>Espera tras la última pulsación antes de buscar los programas.</summary>
    internal TimeSpan DetectionDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Última detección en segundo plano (para las pruebas).</summary>
    internal Task PendingDetection { get; private set; } = Task.CompletedTask;

    private CancellationTokenSource? _mkvDetectCts;
    private CancellationTokenSource? _ffmpegDetectCts;

    private void ScheduleDetection(bool mkv)
    {
        var cts = new CancellationTokenSource();
        ReplaceDetection(mkv, cts);
        var preferred = CleanDirectory(mkv ? MkvToolNixDirectory : FfmpegDirectory);
        PendingDetection = DetectLaterAsync(mkv, preferred, cts.Token);
    }

    private void ReplaceDetection(bool mkv, CancellationTokenSource? next)
    {
        var old = mkv ? _mkvDetectCts : _ffmpegDetectCts;
        old?.Cancel();
        if (mkv)
            _mkvDetectCts = next;
        else
            _ffmpegDetectCts = next;
    }

    private async Task DetectLaterAsync(bool mkv, string? preferred, CancellationToken ct)
    {
        try
        {
            await Task.Delay(DetectionDelay, ct);
            var found = await Task.Run(() => mkv ? _library.DetectMkvToolNix(preferred) : _library.DetectFfmpeg(preferred), ct);
            if (!ct.IsCancellationRequested)
                ApplyDetected(mkv, found, preferred);
        }
        catch (OperationCanceledException)
        {
            // Llegó otra pulsación: vale el resultado de la última.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Ruta inválida o inaccesible: se deja el texto anterior; al guardar se valida.
        }
    }

    private void ApplyDetected(bool mkv, string? found, string? preferred)
    {
        if (mkv)
            DetectedMkvToolNixText = DescribeDetected(found, preferred, "No se encontró MKVToolNix (hace falta para los .mkv).");
        else
            DetectedFfmpegText = DescribeDetected(found, preferred,
                $"No se encontró FFmpeg (solo hace falta para .mp4; winget install {SubtitleTool.Core.Media.FfmpegLocator.WingetPackage}).");
    }

    /// <summary>Qué carpeta se usará con lo escrito ahora (la indicada si contiene los programas; si no, la autodetectada).</summary>
    private void RefreshDetectedMkvToolNix()
    {
        ReplaceDetection(mkv: true, null);
        var preferred = CleanDirectory(MkvToolNixDirectory);
        ApplyDetected(mkv: true, _library.DetectMkvToolNix(preferred), preferred);
    }

    private void RefreshDetectedFfmpeg()
    {
        ReplaceDetection(mkv: false, null);
        var preferred = CleanDirectory(FfmpegDirectory);
        ApplyDetected(mkv: false, _library.DetectFfmpeg(preferred), preferred);
    }

    private static string DescribeDetected(string? found, string? preferred, string notFound) =>
        found is null ? notFound
        : preferred is not null && string.Equals(found.TrimEnd('\\'), preferred.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? $"Se usará: {found}"
        : $"Autodetectado: {found}";

    /// <summary>Clave nueva (la vista la copia desde un PasswordBox; nunca se muestra).</summary>
    public string NewApiKey { get; set; } = "";

    /// <summary>Resultado de cada comprobación (tipo para el icono y texto sin emoji).</summary>
    public ObservableCollection<CheckRow> Checks { get; } = [];

    private void RefreshApiKeyStatus()
    {
        var info = _apiKeys.Resolve();
        ApiKeyStatus = info.Source switch
        {
            ApiKeySource.EnvironmentVariable => $"Variable de entorno {ApiKeyStore.EnvironmentVariable} (…{info.Last4}); tiene prioridad.",
            ApiKeySource.EncryptedFile => $"Guardada cifrada (…{info.Last4}).",
            _ => "No configurada (solo hace falta para el backend API).",
        };
        CanClearApiKey = info.Source == ApiKeySource.EncryptedFile;
    }

    /// <returns>true si se guardó (la vista limpia el PasswordBox).</returns>
    public bool SaveApiKey()
    {
        if (string.IsNullOrWhiteSpace(NewApiKey))
        {
            SetError("Escribe o pega la clave de API antes de guardarla.", SettingsField.ApiKey);
            return false;
        }
        try
        {
            _apiKeys.Save(NewApiKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException
                                       or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            SetError("No se pudo guardar la clave: " + ex.Message, SettingsField.ApiKey);
            return false;
        }
        NewApiKey = "";
        SetError("", null);
        RefreshApiKeyStatus();
        return true;
    }

    [RelayCommand]
    private void ClearApiKey()
    {
        if (!_dialogs.Confirm("¿Borrar la clave de API guardada?"))
            return;
        try
        {
            _apiKeys.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText = "No se pudo borrar la clave: " + ex.Message;
        }
        RefreshApiKeyStatus();
    }

    [RelayCommand]
    private void BrowseMkvToolNix()
    {
        if (_dialogs.PickFolder("Carpeta de MKVToolNix (contiene mkvmerge.exe y mkvextract.exe)") is { } dir)
        {
            MkvToolNixDirectory = dir;
            RefreshDetectedMkvToolNix();
        }
    }

    [RelayCommand]
    private void UseAutodetect()
    {
        MkvToolNixDirectory = "";
        RefreshDetectedMkvToolNix();
    }

    [RelayCommand]
    private void BrowseFfmpeg()
    {
        if (_dialogs.PickFolder("Carpeta de FFmpeg (contiene ffmpeg.exe y ffprobe.exe)") is { } dir)
        {
            FfmpegDirectory = dir;
            RefreshDetectedFfmpeg();
        }
    }

    [RelayCommand]
    private void UseAutodetectFfmpeg()
    {
        FfmpegDirectory = "";
        RefreshDetectedFfmpeg();
    }

    private static string? CleanDirectory(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim().Trim('"');

    /// <summary>Lo mismo que <c>subtool doctor</c>.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        using var cts = new CancellationTokenSource();
        _checkCts = cts;
        IsChecking = true;
        Checks.Clear();
        SetCheckStatus("Comprobando… (incluye una llamada mínima a Claude Code con haiku; puede tardar unos segundos)", StatusKind.InProgress);
        try
        {
            var checks = await _doctor.RunAsync(new SubtitleTool.Core.Media.MediaToolDirectories(
                CleanDirectory(MkvToolNixDirectory), CleanDirectory(FfmpegDirectory)), cts.Token);
            foreach (var c in checks)
                Checks.Add(new CheckRow(c.Ok ? StatusKind.Success : c.Required ? StatusKind.Error : StatusKind.Warning, c.Text));
            if (DoctorService.AllOk(checks))
                SetCheckStatus("Todo listo.", StatusKind.Success);
            else
                SetCheckStatus("Hay puntos que revisar.", StatusKind.Error);
        }
        catch (OperationCanceledException)
        {
            SetCheckStatus("Comprobación cancelada.", StatusKind.Info);
        }
        catch (Exception ex)
        {
            SetCheckStatus("Error al comprobar: " + ex.Message, StatusKind.Error);
        }
        finally
        {
            _checkCts = null;
            IsChecking = false;
        }
    }

    private void SetCheckStatus(string text, StatusKind kind)
    {
        CheckStatus = text;
        CheckStatusKind = kind;
    }

    /// <summary>Detiene la comprobación en curso (al cerrar la ventana no se deja trabajando en segundo plano).</summary>
    public void CancelPendingWork()
    {
        _checkCts?.Cancel();
        ReplaceDetection(mkv: true, null);
        ReplaceDetection(mkv: false, null);
    }

    private bool CanCheck() => !IsChecking;

    /// <summary>Valida y prepara <see cref="Result"/>. Devuelve false (con <see cref="ErrorText"/>) si hay valores no válidos.</summary>
    public bool TrySave()
    {
        if (!int.TryParse(ChunkSizeText?.Trim(), out var chunk) || chunk < 1 || chunk > 2000)
        {
            SetError("El tamaño de tramo debe ser un número entero entre 1 y 2000.", SettingsField.ChunkSize);
            return false;
        }
        if (!int.TryParse(TimeoutText?.Trim(), out var timeout) || timeout < 1 || timeout > 240)
        {
            SetError("El tiempo máximo por tramo debe ser un número entero de minutos entre 1 y 240.", SettingsField.Timeout);
            return false;
        }
        var dir = CleanDirectory(MkvToolNixDirectory);
        if (dir is not null && !Directory.Exists(dir))
        {
            SetError($"La carpeta de MKVToolNix no existe: {dir}", SettingsField.MkvToolNixDirectory);
            return false;
        }
        var ffmpegDir = CleanDirectory(FfmpegDirectory);
        if (ffmpegDir is not null && !Directory.Exists(ffmpegDir))
        {
            SetError($"La carpeta de FFmpeg no existe: {ffmpegDir}", SettingsField.FfmpegDirectory);
            return false;
        }

        Result = new AppSettings
        {
            Backend = Backend,
            Model = Model,
            Effort = Effort,
            ChunkSize = chunk,
            TimeoutMinutes = timeout,
            CreatePlainCopy = CreatePlainCopy,
            CheckForUpdates = CheckForUpdates,
            ReviewBeforeSave = ReviewBeforeSave,
            MkvToolNixDirectory = dir,
            FfmpegDirectory = ffmpegDir,
        }.Normalize();
        SetError("", null);
        return true;
    }

    private void SetError(string text, SettingsField? field)
    {
        ErrorField = field;
        ErrorText = text;
    }
}

/// <summary>Campos de Ajustes que pueden tener un error de validación.</summary>
public enum SettingsField
{
    ChunkSize,
    Timeout,
    MkvToolNixDirectory,
    FfmpegDirectory,
    ApiKey,
}
