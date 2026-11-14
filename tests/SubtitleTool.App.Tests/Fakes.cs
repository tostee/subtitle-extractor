using SubtitleTool.App.Models;
using SubtitleTool.App.Services;
using SubtitleTool.App.ViewModels;
using SubtitleTool.Core.Dubbing;
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
        if (MkvMissing && !VideoFormats.IsMp4(videoPath))
            return Task.FromResult(new VideoStatus { VideoPath = videoPath, Error = "No se encontró MKVToolNix (prueba).", MkvToolNixMissing = true });
        return Task.FromResult(Statuses.TryGetValue(videoPath, out var f) ? f() : TestVideos.Translatable(videoPath));
    }

    public string? DetectMkvToolNix(string? preferred = null) => MkvMissing ? null : MkvDetected;
    public string MkvDetected { get; set; } = @"C:\Program Files\MKVToolNix";
    private int _ffmpegDetections;
    /// <summary>Veces que se buscó FFmpeg (para comprobar la espera al escribir la ruta).</summary>
    public int FfmpegDetections => Volatile.Read(ref _ffmpegDetections);
    public string? DetectFfmpeg(string? preferred = null)
    {
        Interlocked.Increment(ref _ffmpegDetections);
        return FfmpegDetected;
    }
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
    /// <summary>Si se indica, «Regenerar» espera a que se complete (o a que se cancele).</summary>
    public TaskCompletionSource? RegenerateGate { get; set; }

    public Glossary? Load(string glossaryPath)
    {
        if (Invalid.Contains(glossaryPath))
            throw new InvalidDataException("El glosario no es JSON válido (prueba).");
        return Files.TryGetValue(glossaryPath, out var g) ? g : null;
    }

    public void Save(string glossaryPath, Glossary glossary) => Files[glossaryPath] = glossary;

    public async Task<Glossary> RegenerateAsync(string englishPath, AppSettings settings, CancellationToken ct)
    {
        RegenerateCount++;
        if (RegenerateGate is not null)
            await RegenerateGate.Task.WaitAsync(ct);
        var g = new Glossary { Terms = [new GlossaryTerm { En = "Ithaca", Es = "Ítaca" }] };
        Files[TranslationPaths.FromEnglish(englishPath).GlossaryPath] = g;
        return g;
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

    /// <summary>Si se indica, la comprobación espera a que se complete (o a que se cancele).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<IReadOnlyList<DoctorCheck>> RunAsync(MediaToolDirectories tools, CancellationToken ct = default)
    {
        LastTools = tools;
        if (Gate is not null)
            await Gate.Task.WaitAsync(ct);
        return Result;
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
    /// <summary>Respuesta a «¿Guardar los cambios?»: true guardar, false no guardar, null cancelar.</summary>
    public bool? AskSaveResult { get; set; }
    public List<string> AskSaves { get; } = [];
    public bool? AskSaveChanges(string message, string title = "Subtítulos")
    {
        AskSaves.Add(message);
        return AskSaveResult;
    }
    public void OpenFolder(string path) => OpenedFolders.Add(path);
    public void OpenFile(string path) { }
    public List<string> OpenedUrls { get; } = [];
    public void OpenUrl(string url) => OpenedUrls.Add(url);
    public List<bool> TextProse { get; } = [];
    public void ShowText(string title, string text, bool prose = false)
    {
        Texts.Add((title, text));
        TextProse.Add(prose);
    }
    public bool ShowSettings(SettingsViewModel viewModel) => OnSettings?.Invoke(viewModel) ?? false;
    public void ShowGlossaryEditor(GlossaryEditorViewModel viewModel) => GlossaryShown = viewModel;
    /// <summary>Lo que hace el usuario en la ventana de revisión; la ventana real es modal, así que se espera a que termine.</summary>
    public Func<ReviewViewModel, Task>? OnReview { get; set; }
    public ReviewViewModel? ReviewShown { get; private set; }
    public void ShowReview(ReviewViewModel viewModel)
    {
        ReviewShown = viewModel;
        OnReview?.Invoke(viewModel).GetAwaiter().GetResult();
    }
    public void ShowSummary(IReadOnlyList<SummaryRow> rows) => Summaries.Add(rows);
    public DubbingViewModel? DubbingShown { get; private set; }
    public void ShowDubbing(DubbingViewModel viewModel) => DubbingShown = viewModel;
    public string? AudioToPick { get; set; }
    public string? PickAudioFile(string title) => AudioToPick;
    public string? VideoToPick { get; set; }
    public string? PickVideoFile() => VideoToPick;
}

/// <summary>Doblaje falso: proyectos en memoria, motores marcados a mano y etiquetado/generación simulados.</summary>
public sealed class FakeDubbing : IDubbingService
{
    public HashSet<DubbingEngine> Installed { get; } = [];
    public Dictionary<string, IReadOnlyList<(string Path, string Label)>> Subtitles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DubbingProject> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DubbingProject> Saved { get; } = [];
    public List<DubbingEngine> InstallCalls { get; } = [];
    public List<DubbingRequest> Requests { get; } = [];
    public List<AppSettings> SettingsSeen { get; } = [];
    public Exception? RunError { get; set; }
    public TaskCompletionSource? RunGate { get; set; }
    /// <summary>Etiquetas que «devuelve Claude»: id → (personaje, sexo, emoción).</summary>
    public Dictionary<int, (string Speaker, string Gender, string Emotion)> Annotations { get; } = [];

    public string EnginesRoot(AppSettings settings) => settings.DubbingEnginesDirectory ?? @"C:\voces";
    public bool IsInstalled(DubbingEngine engine, AppSettings settings) => Installed.Contains(engine);
    public Task<string?> DetectGpuAsync(CancellationToken ct = default) => Task.FromResult<string?>("GPU de prueba");

    public Task InstallAsync(DubbingEngine engine, AppSettings settings, IProgress<string> log, CancellationToken ct)
    {
        InstallCalls.Add(engine);
        log.Report("instalando (prueba)");
        Installed.Add(engine);
        return Task.CompletedTask;
    }

    public IReadOnlyList<(string Path, string Label)> FindSubtitles(string videoPath) =>
        Subtitles.TryGetValue(videoPath, out var s) ? s : [];

    public DubbingProject LoadProject(string subtitlePath) =>
        Projects.TryGetValue(subtitlePath, out var p) ? p : throw new FileNotFoundException(subtitlePath);

    public void SaveProject(DubbingProject project) => Saved.Add(project);

    public Task<TokenUsage> AnnotateAsync(DubbingProject project, AppSettings settings, IProgress<AnnotationProgress> progress, CancellationToken ct)
    {
        SettingsSeen.Add(settings);
        var genders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in project.Lines)
            if (Annotations.TryGetValue(line.Id, out var a))
            {
                line.Speaker = a.Speaker;
                line.Emotion = a.Emotion;
                genders[a.Speaker] = a.Gender;
            }
        project.SyncCharacters(genders);
        project.Annotated = true;
        progress.Report(new AnnotationProgress(project.Lines.Count, project.Lines.Count, TokenUsage.Zero));
        return Task.FromResult(new TokenUsage(100, 50, 0, 0));
    }

    public async Task<DubbingResult> RunAsync(DubbingRequest request, AppSettings settings, IProgress<DubbingProgress> progress, CancellationToken ct)
    {
        Requests.Add(request);
        SettingsSeen.Add(settings);
        progress.Report(new DubbingProgress("voces", 1, 2, "voz 1"));
        if (RunGate is not null)
            await RunGate.Task.WaitAsync(ct);
        if (RunError is not null)
            throw RunError;
        return new DubbingResult(DubbingRunner.OutputPathFor(request.VideoPath, request.Range), request.Project.Lines.Count, 1, 0, 0.1);
    }
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
    public FakeDubbing Dubbing { get; } = new();
    /// <summary>Borradores en una carpeta temporal (nunca en %LOCALAPPDATA%).</summary>
    public string ReviewRoot { get; } = Path.Combine(Path.GetTempPath(), "subtool-review-tests-" + Guid.NewGuid().ToString("N"));
    public ReviewServiceAdapter Reviews => _reviews ??= new ReviewServiceAdapter(new SubtitleTool.Core.Review.ReviewStore(ReviewRoot));
    private ReviewServiceAdapter? _reviews;

    public MainViewModel CreateMain() => new(Settings, Library, Processing, Glossary, Doctor, ApiKeys, Dialogs, ActivityLog, Updates, Reviews, Dubbing);

    /// <summary>Carpeta falsa con N videos.</summary>
    public IReadOnlyList<string> AddFolder(string folder, params string[] names)
    {
        var videos = names.Select(n => Path.Combine(folder, n)).ToList();
        Library.Folders[folder] = videos;
        return videos;
    }
}
