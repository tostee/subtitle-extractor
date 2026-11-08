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
        var detected = library.DetectMkvToolNix();
        DetectedMkvToolNixText = detected is null
            ? "Autodetección: no se encontró MKVToolNix."
            : $"Autodetectado: {detected}";
        var detectedFfmpeg = library.DetectFfmpeg();
        DetectedFfmpegText = detectedFfmpeg is null
            ? $"Autodetección: no se encontró FFmpeg (solo hace falta para .mp4; winget install {SubtitleTool.Core.Media.FfmpegLocator.WingetPackage})."
            : $"Autodetectado: {detectedFfmpeg}";
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
    [ObservableProperty] private string _apiKeyStatus = "";
    [ObservableProperty] private bool _canClearApiKey;
    [ObservableProperty] private string _checkStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private bool _isChecking;

    public string DetectedMkvToolNixText { get; }
    public string DetectedFfmpegText { get; }

    /// <summary>Clave nueva (la vista la copia desde un PasswordBox; nunca se muestra).</summary>
    public string NewApiKey { get; set; } = "";

    public ObservableCollection<DoctorCheck> Checks { get; } = [];

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
            ErrorText = "Escribe o pega la clave de API antes de guardarla.";
            return false;
        }
        try
        {
            _apiKeys.Save(NewApiKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException
                                       or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            ErrorText = "No se pudo guardar la clave: " + ex.Message;
            return false;
        }
        NewApiKey = "";
        ErrorText = "";
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
            MkvToolNixDirectory = dir;
    }

    [RelayCommand]
    private void UseAutodetect() => MkvToolNixDirectory = "";

    [RelayCommand]
    private void BrowseFfmpeg()
    {
        if (_dialogs.PickFolder("Carpeta de FFmpeg (contiene ffmpeg.exe y ffprobe.exe)") is { } dir)
            FfmpegDirectory = dir;
    }

    [RelayCommand]
    private void UseAutodetectFfmpeg() => FfmpegDirectory = "";

    private static string? CleanDirectory(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim().Trim('"');

    /// <summary>Lo mismo que <c>subtool doctor</c>.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        IsChecking = true;
        Checks.Clear();
        CheckStatus = "Comprobando… (incluye una llamada mínima a Claude Code con haiku)";
        try
        {
            var checks = await _doctor.RunAsync(new SubtitleTool.Core.Media.MediaToolDirectories(
                CleanDirectory(MkvToolNixDirectory), CleanDirectory(FfmpegDirectory)));
            foreach (var c in checks)
                Checks.Add(c);
            CheckStatus = DoctorService.AllOk(checks) ? "✅ Todo listo." : "❌ Hay puntos que revisar.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CheckStatus = "❌ Error al comprobar: " + ex.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private bool CanCheck() => !IsChecking;

    /// <summary>Valida y prepara <see cref="Result"/>. Devuelve false (con <see cref="ErrorText"/>) si hay valores no válidos.</summary>
    public bool TrySave()
    {
        if (!int.TryParse(ChunkSizeText?.Trim(), out var chunk) || chunk < 1 || chunk > 2000)
        {
            ErrorText = "El tamaño de tramo debe ser un número entero entre 1 y 2000.";
            return false;
        }
        if (!int.TryParse(TimeoutText?.Trim(), out var timeout) || timeout < 1 || timeout > 240)
        {
            ErrorText = "El tiempo máximo por tramo debe ser un número entero de minutos entre 1 y 240.";
            return false;
        }
        var dir = CleanDirectory(MkvToolNixDirectory);
        if (dir is not null && !Directory.Exists(dir))
        {
            ErrorText = $"La carpeta de MKVToolNix no existe: {dir}";
            return false;
        }
        var ffmpegDir = CleanDirectory(FfmpegDirectory);
        if (ffmpegDir is not null && !Directory.Exists(ffmpegDir))
        {
            ErrorText = $"La carpeta de FFmpeg no existe: {ffmpegDir}";
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
        ErrorText = "";
        return true;
    }
}
