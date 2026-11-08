using SubtitleTool.App.Models;
using SubtitleTool.App.Services;
using SubtitleTool.App.ViewModels;
using SubtitleTool.Core.Library;
using SubtitleTool.Core.Media;
using SubtitleTool.Core.Mkv;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Updates;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.App.Tests;

/// <summary>Estados de video de prueba (sin MKVToolNix).</summary>
public static class TestVideos
{
    public static VideoStatus Translatable(string path, bool englishExists = true, bool spanishExists = false)
    {
        var track = new MkvTrack { Id = 3, Type = "subtitles", Codec = "SubRip/SRT", CodecId = "S_TEXT/UTF8", Language = "eng" };
        var baseName = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        return new VideoStatus
        {
            VideoPath = path,
            SubtitleTracks = [track],
            Selection = new TrackSelection(track, SubtitleKind.Normal, SelectionFailure.None),
            EnglishPath = baseName + ".en.srt",
            EnglishExists = englishExists,
            SpanishExisting = spanishExists ? [baseName + ".es.srt"] : [],
        };
    }

    public static VideoStatus ImageOnly(string path) => new()
    {
        VideoPath = path,
        Selection = TrackSelection.Fail(SelectionFailure.ImageOnly),
    };
}

public sealed class FakeSettingsStore : ISettingsStore
{
    public AppSettings Stored { get; set; } = new();
    public int SaveCount { get; private set; }
    public AppSettings Load() => Stored.Clone();
    public void Save(AppSettings settings)
    {
        Stored = settings.Clone();
        SaveCount++;
    }
}

public sealed class FakeLibrary : ILibraryService
{
    public Dictionary<string, IReadOnlyList<string>> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Func<VideoStatus>> Statuses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool MkvMissing { get; set; }
    public int ScanCount { get; private set; }

    public IReadOnlyList<string> FindVideos(string path) =>
        Folders.TryGetValue(path, out var v) ? v : throw new FileNotFoundException($"No existe: {path}");

    public MediaToolDirectories? LastTools { get; private set; }
    public string? FfmpegDetected { get; set; } = @"C:\ffmpeg\bin";

    public Task<VideoStatus> ScanAsync(string videoPath, MediaToolDirectories tools, CancellationToken ct = default)
    {
        ScanCount++;
        LastTools = tools;
        if (MkvMissing)
            throw new MkvToolNixNotFoundException("No se encontró MKVToolNix (prueba).");
        return Task.FromResult(Statuses.TryGetValue(videoPath, out var f) ? f() : TestVideos.Translatable(videoPath));
    }

    public string? DetectMkvToolNix(string? preferred = null) => MkvMissing ? null : @"C:\Program Files\MKVToolNix";
    public string? DetectFfmpeg(string? preferred = null) => FfmpegDetected;
}

/// <summary>Procesador falso: por defecto completa al instante; <see cref="Handler"/> permite simular otros casos.</summary>
public sealed class FakeProcessing : IVideoProcessingService
{
    private int _running;
    public int MaxConcurrency { get; private set; }
    public List<string> Processed { get; } = [];
    public List<AppSettings> SettingsSeen { get; } = [];
    public Func<VideoStatus, IProgress<VideoProgress>, CancellationToken, Task<VideoOutcome>>? Handler { get; set; }

    public async Task<VideoOutcome> ProcessAsync(VideoStatus status, AppSettings settings, IProgress<VideoProgress> progress, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _running);
        MaxConcurrency = Math.Max(MaxConcurrency, now);
        try
        {
            Processed.Add(Path.GetFileName(status.VideoPath));
            SettingsSeen.Add(settings);
            await Task.Yield();
            if (Handler is not null)
                return await Handler(status, progress, ct);
            return new VideoOutcome
            {
                Status = VideoOutcomeStatus.Completed, Detail = "10 bloques, suscripción (≈$0.01 API)",
                Usage = new TokenUsage(1000, 500, 0, 0), CostUsd = 0.01m,
            };
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

public sealed class FakeGlossaryService : IGlossaryService
{
    public Dictionary<string, Glossary> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Invalid { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int RegenerateCount { get; private set; }

    public Glossary? Load(string glossaryPath)
    {
        if (Invalid.Contains(glossaryPath))
            throw new InvalidDataException("El glosario no es JSON válido (prueba).");
        return Files.TryGetValue(glossaryPath, out var g) ? g : null;
    }

    public void Save(string glossaryPath, Glossary glossary) => Files[glossaryPath] = glossary;

    public Task<Glossary> RegenerateAsync(string englishPath, AppSettings settings, CancellationToken ct)
    {
        RegenerateCount++;
        var g = new Glossary { Terms = [new GlossaryTerm { En = "Ithaca", Es = "Ítaca" }] };
        Files[TranslationPaths.FromEnglish(englishPath).GlossaryPath] = g;
        return Task.FromResult(g);
    }
}

public sealed class FakeDoctor : IDoctorService
{
    public MediaToolDirectories? LastTools { get; private set; }
    public IReadOnlyList<DoctorCheck> Result { get; set; } =
    [
        new(true, "MKVToolNix: C:\\Program Files\\MKVToolNix"),
        new(true, "Claude Code: 2.0.0 (claude.exe)"),
        new(false, "Clave de API: no configurada", Required: false),
    ];

    public Task<IReadOnlyList<DoctorCheck>> RunAsync(MediaToolDirectories tools, CancellationToken ct = default)
    {
        LastTools = tools;
        return Task.FromResult(Result);
    }
}

public sealed class FakeApiKeys : IApiKeyService
{
    public string? Key { get; set; }
    public ApiKeyInfo Resolve() => Key is null ? new ApiKeyInfo(ApiKeySource.None, null) : new ApiKeyInfo(ApiKeySource.EncryptedFile, Key);
    public void Save(string key) => Key = key.Trim();
    public bool Clear()
    {
        var had = Key is not null;
        Key = null;
        return had;
    }
}

public sealed class FakeDialogs : IDialogService
{
    public string? FolderToPick { get; set; }
    public List<string> FilesToPick { get; } = [];
    public List<string> Infos { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];
    public bool ConfirmResult { get; set; } = true;
    public List<IReadOnlyList<SummaryRow>> Summaries { get; } = [];
    public List<string> OpenedFolders { get; } = [];
    public List<(string Title, string Text)> Texts { get; } = [];
    public GlossaryEditorViewModel? GlossaryShown { get; private set; }
    public Func<SettingsViewModel, bool>? OnSettings { get; set; }

    public string? PickFolder(string title) => FolderToPick;
    public IReadOnlyList<string> PickVideoFiles() => FilesToPick;
    public void ShowInfo(string message, string title = "Subtítulos") => Infos.Add(message);
    public void ShowWarning(string message, string title = "Subtítulos") => Warnings.Add(message);
    public void ShowError(string message, string title = "Subtítulos") => Errors.Add(message);
    public bool Confirm(string message, string title = "Subtítulos") => ConfirmResult;
    public void OpenFolder(string path) => OpenedFolders.Add(path);
    public void OpenFile(string path) { }
    public List<string> OpenedUrls { get; } = [];
    public void OpenUrl(string url) => OpenedUrls.Add(url);
    public void ShowText(string title, string text) => Texts.Add((title, text));
    public bool ShowSettings(SettingsViewModel viewModel) => OnSettings?.Invoke(viewModel) ?? false;
    public void ShowGlossaryEditor(GlossaryEditorViewModel viewModel) => GlossaryShown = viewModel;
    public void ShowSummary(IReadOnlyList<SummaryRow> rows) => Summaries.Add(rows);
}

public sealed class FakeUpdates : IUpdateService
{
    public string CurrentVersion => "1.0.0";
    public bool CanSelfUpdate { get; set; } = true;
    public ReleaseInfo? Newer { get; set; }
    public Exception? CheckError { get; set; }
    public Exception? DownloadError { get; set; }
    public int Checks { get; private set; }
    public string? Installed { get; private set; }

    public static ReleaseInfo Release(string version) => new(Version.Parse(version), "v" + version, "v" + version, "- Novedad",
        "https://github.com/tostee/subtitle-extractor/releases/tag/v" + version, "https://x/Subtitulos-Setup.exe", "Subtitulos-Setup.exe", 10, null);

    public Task<ReleaseInfo?> FindNewerAsync(CancellationToken ct = default)
    {
        Checks++;
        return CheckError is not null ? Task.FromException<ReleaseInfo?>(CheckError) : Task.FromResult(Newer);
    }

    public Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken ct = default)
    {
        if (DownloadError is not null)
            return Task.FromException<string>(DownloadError);
        progress.Report(1);
        return Task.FromResult(@"C:\tmp\Subtitulos-Setup.exe");
    }

    public void InstallAndExit(string installerPath) => Installed = installerPath;
}

public sealed class FakeActivityLog : IActivityLog
{
    public List<string> Lines { get; } = [];
    public string FolderPath => @"C:\logs";
    public void Write(string message) => Lines.Add(message);
    public void WriteException(string context, Exception exception) => Lines.Add($"ERROR {context}: {exception}");
}

/// <summary>Conjunto de servicios falsos y la ViewModel principal.</summary>
public sealed class Harness
{
    public FakeSettingsStore Settings { get; } = new();
    public FakeLibrary Library { get; } = new();
    public FakeProcessing Processing { get; } = new();
    public FakeGlossaryService Glossary { get; } = new();
    public FakeDoctor Doctor { get; } = new();
    public FakeApiKeys ApiKeys { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public FakeActivityLog ActivityLog { get; } = new();
    public FakeUpdates Updates { get; } = new();

    public MainViewModel CreateMain() => new(Settings, Library, Processing, Glossary, Doctor, ApiKeys, Dialogs, ActivityLog, Updates);

    /// <summary>Carpeta falsa con N videos.</summary>
    public IReadOnlyList<string> AddFolder(string folder, params string[] names)
    {
        var videos = names.Select(n => Path.Combine(folder, n)).ToList();
        Library.Folders[folder] = videos;
        return videos;
    }
}
