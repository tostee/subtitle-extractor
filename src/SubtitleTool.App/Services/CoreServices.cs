using System.Text;
using System.Text.Json;
using SubtitleTool.App.Models;
using SubtitleTool.Core.Diagnostics;
using SubtitleTool.Core.Library;
using SubtitleTool.Core.Media;
using SubtitleTool.Core.Mkv;
using SubtitleTool.Core.Srt;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Updates;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.App.Services;

/// <summary>Ajustes en <c>%LOCALAPPDATA%\SubtitleTool\settings.json</c>.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath { get; }

    public JsonSettingsStore(string? filePath = null) => FilePath = filePath ?? AppPaths.SettingsFile;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath, Encoding.UTF8), Options) ?? new AppSettings()).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Ajustes dañados o ilegibles: se usan los predeterminados.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options), new UTF8Encoding(false));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

public sealed class LibraryService : ILibraryService
{
    public IReadOnlyList<string> FindVideos(string path) => LibraryScanner.FindVideos(path);

    public Task<VideoStatus> ScanAsync(string videoPath, MediaToolDirectories tools, CancellationToken ct = default) =>
        Task.Run(() => new LibraryScanner(new MediaToolkit(tools)).ScanVideoAsync(videoPath, null, ct), ct);

    public string? DetectMkvToolNix(string? preferred = null)
    {
        try
        {
            return Path.GetDirectoryName(MkvToolNixLocator.LocatePreferring(preferred).MkvMerge);
        }
        catch (MkvToolNixNotFoundException)
        {
            return null;
        }
    }

    public string? DetectFfmpeg(string? preferred = null)
    {
        try
        {
            return Path.GetDirectoryName(FfmpegLocator.LocatePreferring(preferred).FFmpeg);
        }
        catch (FfmpegNotFoundException)
        {
            return null;
        }
    }
}

public sealed class VideoProcessingService : IVideoProcessingService
{
    public Task<VideoOutcome> ProcessAsync(VideoStatus status, AppSettings settings, IProgress<VideoProgress> progress, CancellationToken ct)
    {
        var tools = settings.Tools;
        var processor = new VideoProcessor(
            () => new MediaToolkit(tools),
            o => TranslationClientFactory.Create(o.Translation.Backend, o.Translation.Model, o.Translation.Effort, o.Timeout));
        // Task.Run: el análisis del SRT y el estado de reanudación hacen E/S síncrona; nunca en el hilo de la interfaz.
        return Task.Run(() => processor.ProcessAsync(status, settings.ToProcessingOptions(), progress, ct), CancellationToken.None);
    }
}

public sealed class GlossaryService : IGlossaryService
{
    public Glossary? Load(string glossaryPath)
    {
        if (!File.Exists(glossaryPath))
            return null;
        return TranslationPrompts.ParseGlossary(File.ReadAllText(glossaryPath, Encoding.UTF8))
               ?? throw new InvalidDataException($"El glosario no es JSON válido: {glossaryPath}");
    }

    public void Save(string glossaryPath, Glossary glossary)
    {
        var json = JsonSerializer.Serialize(glossary, TranslationPrompts.IndentedJsonOptions);
        if (TranslationPrompts.ParseGlossary(json) is null)
            throw new InvalidDataException("El glosario generado no es JSON válido.");
        var tmp = glossaryPath + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, glossaryPath, overwrite: true);
    }

    public Task<Glossary> RegenerateAsync(string englishPath, AppSettings settings, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var paths = TranslationPaths.FromEnglish(englishPath);
            var o = settings.ToProcessingOptions();
            var client = TranslationClientFactory.Create(o.Translation.Backend, o.Translation.Model, o.Translation.Effort, o.Timeout)
                         ?? throw new TranslationClientException(TranslationClientFactory.MissingApiKeyMessage);
            var doc = await SrtParser.ParseFileAsync(paths.EnglishPath, ct).ConfigureAwait(false);
            var translator = new SubtitleTranslator(client, o.Translation);
            return await translator.GetOrCreateGlossaryAsync(paths, doc, regenerate: true, ct).ConfigureAwait(false);
        }, ct);
}

public sealed class DoctorServiceAdapter : IDoctorService
{
    public Task<IReadOnlyList<DoctorCheck>> RunAsync(MediaToolDirectories tools, CancellationToken ct = default) =>
        Task.Run(() => new DoctorService(tools).RunAsync(ct), ct);
}

public sealed class GitHubUpdateService : IUpdateService
{
    private readonly GitHubUpdater _updater = new();

    public string CurrentVersion => GitHubUpdater.CurrentVersion.ToString(3);
    public bool CanSelfUpdate => GitHubUpdater.IsInstalled();

    public Task<ReleaseInfo?> FindNewerAsync(CancellationToken ct = default) => Task.Run(() => _updater.FindNewerAsync(ct), ct);

    public Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken ct = default) =>
        Task.Run(() => _updater.DownloadInstallerAsync(release, progress, ct), ct);

    public void InstallAndExit(string installerPath)
    {
        try
        {
            GitHubUpdater.LaunchInstaller(installerPath, relaunchApp: true);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new UpdateException("No se inició el instalador: " + ex.Message, ex);
        }
        System.Windows.Application.Current.Shutdown();
    }
}

/// <summary>Sin actualizaciones (pruebas).</summary>
public sealed class NullUpdateService : IUpdateService
{
    public static NullUpdateService Instance { get; } = new();
    public string CurrentVersion => "0.0.0";
    public bool CanSelfUpdate => false;
    public Task<ReleaseInfo?> FindNewerAsync(CancellationToken ct = default) => Task.FromResult<ReleaseInfo?>(null);
    public Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken ct = default) =>
        throw new UpdateException("Sin actualizaciones.");
    public void InstallAndExit(string installerPath) { }
}

/// <summary>Registro diario compartido con la CLI (<see cref="AppLog"/>).</summary>
public sealed class FileActivityLog : IActivityLog
{
    private readonly AppLog _log;
    public FileActivityLog(AppLog? log = null) => _log = log ?? AppLog.Default;
    public string FolderPath => _log.Directory;
    public void Write(string message) => _log.Write("app", message);
    public void WriteException(string context, Exception exception) => _log.WriteException("app", context, exception);
}

/// <summary>Registro que no escribe nada (pruebas).</summary>
public sealed class NullActivityLog : IActivityLog
{
    public static NullActivityLog Instance { get; } = new();
    public string FolderPath => Path.GetTempPath();
    public void Write(string message) { }
    public void WriteException(string context, Exception exception) { }
}

public sealed class ApiKeyService : IApiKeyService
{
    private readonly ApiKeyStore _store = new();
    public ApiKeyInfo Resolve() => _store.Resolve();
    public void Save(string key) => _store.Save(key);
    public bool Clear() => _store.Clear();
}
