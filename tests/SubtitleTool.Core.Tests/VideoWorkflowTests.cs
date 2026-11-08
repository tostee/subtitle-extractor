using System.Text;
using SubtitleTool.Core.Library;
using SubtitleTool.Core.Mkv;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.Core.Tests;

public sealed class VideoWorkflowTests : IDisposable
{
    private readonly string _dir;
    private readonly string _jobs;
    private readonly string _logs;

    public VideoWorkflowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "subtool-wf-tests-" + Guid.NewGuid().ToString("N"));
        _jobs = Path.Combine(_dir, "_jobs");
        _logs = Path.Combine(_dir, "_logs");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly MkvTrack SrtTrack = new() { Id = 3, Type = "subtitles", CodecId = "S_TEXT/UTF8", Language = "eng" };

    private VideoStatus Status(string name = "Movie.mkv", bool englishExists = true, IReadOnlyList<string>? spanish = null,
        TrackSelection? selection = null, string? englishExt = ".en.srt", string? error = null)
    {
        var video = Path.Combine(_dir, name);
        var baseName = Path.Combine(_dir, Path.GetFileNameWithoutExtension(name));
        return new VideoStatus
        {
            VideoPath = video,
            Selection = selection ?? new TrackSelection(SrtTrack, SubtitleKind.Normal, SelectionFailure.None),
            EnglishPath = selection is { Track: null } || englishExt is null ? null : baseName + englishExt,
            EnglishExists = englishExists,
            SpanishExisting = spanish ?? [],
            Error = error,
        };
    }

    [Fact]
    public void Plan_CoversAllCases()
    {
        Assert.Equal(VideoPlanKind.InspectError, VideoWorkflow.Plan(Status(error: "boom"), false).Kind);
        var already = VideoWorkflow.Plan(Status(spanish: [Path.Combine(_dir, "Movie.es.sdh.srt")]), false);
        Assert.Equal(VideoPlanKind.AlreadyTranslated, already.Kind);
        Assert.Equal("ya tiene .es.sdh.srt", already.Reason);
        Assert.Equal(VideoPlanKind.Process, VideoWorkflow.Plan(Status(spanish: [Path.Combine(_dir, "Movie.es.srt")]), true).Kind);
        Assert.Equal("inglés solo en imagen (necesita OCR)",
            VideoWorkflow.Plan(Status(selection: TrackSelection.Fail(SelectionFailure.ImageOnly)), false).Reason);
        Assert.Equal(VideoPlanKind.NoEnglish, VideoWorkflow.Plan(Status(selection: TrackSelection.Fail(SelectionFailure.NoEnglish)), false).Kind);
        var ass = VideoWorkflow.Plan(Status(englishExt: ".en.ass"), false);
        Assert.Equal(VideoPlanKind.UnsupportedFormat, ass.Kind);
        Assert.Equal("formato .ass no soportado para traducir", ass.Reason);
        Assert.True(VideoWorkflow.Plan(Status(englishExists: false), false).NeedsExtraction);
        Assert.False(VideoWorkflow.Plan(Status(), false).NeedsExtraction);
    }

    [Fact]
    public void FormatCost_SubscriptionShowsApiEquivalent()
    {
        Assert.Equal("incluido en la suscripción (equivalente API ~$1.2346)", VideoWorkflow.FormatCost(1.23456m, claudeCode: true));
        Assert.Equal("coste estimado $0.5000", VideoWorkflow.FormatCost(0.5m, claudeCode: false));
    }

    private VideoProcessor Processor(Func<VideoProcessingOptions, ITranslationClient?> client) =>
        new(() => throw new InvalidOperationException("no debe extraer"), client, _jobs, _logs);

    private string WriteEnglish(string name = "Movie.en.srt", int blocks = 30)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, SubtitleTranslatorTests.MakeSrt(blocks), new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public async Task Processor_TranslatesWithFakeClient_AndReportsProgress()
    {
        WriteEnglish();
        var reports = new List<VideoProgress>();
        var options = new VideoProcessingOptions { Translation = new TranslationOptions { ChunkSize = 10 } };
        var outcome = await Processor(_ => new FakeTranslationClient())
            .ProcessAsync(Status(), options, new ListProgress(reports));

        Assert.Equal(VideoOutcomeStatus.Completed, outcome.Status);
        Assert.True(File.Exists(Path.Combine(_dir, "Movie.es.srt")));
        Assert.Contains("30 bloques, suscripción", outcome.Detail);
        Assert.Contains(reports, r => r.Translation is { ChunkCount: 3 });
        Assert.NotNull(outcome.LogPath);
        Assert.StartsWith(_logs, outcome.LogPath);
    }

    [Fact]
    public async Task Processor_SkipsAlreadyTranslated_WithoutCreatingClient()
    {
        var outcome = await Processor(_ => throw new InvalidOperationException("no debe crear cliente"))
            .ProcessAsync(Status(spanish: [Path.Combine(_dir, "Movie.es.srt")]), new VideoProcessingOptions());
        Assert.Equal(VideoOutcomeStatus.Skipped, outcome.Status);
        Assert.Equal("ya tiene .es.srt", outcome.Detail);
    }

    [Fact]
    public async Task Processor_UsageLimit_StopsBatch()
    {
        WriteEnglish();
        var client = new FakeTranslationClient
        {
            Handler = (_, _) => throw new TranslationClientException("Límite de uso alcanzado.", isUsageLimit: true),
        };
        var outcome = await Processor(_ => client).ProcessAsync(Status(), new VideoProcessingOptions());
        Assert.Equal(VideoOutcomeStatus.Failed, outcome.Status);
        Assert.True(outcome.IsUsageLimit);
        Assert.True(outcome.StopsBatch);
        Assert.Equal("límite de uso alcanzado (reanudable)", outcome.Detail);
    }

    [Fact]
    public async Task Processor_MissingApiKey_IsConfigurationError()
    {
        WriteEnglish();
        var options = new VideoProcessingOptions { Translation = new TranslationOptions { Backend = TranslationBackends.Api } };
        var outcome = await Processor(_ => null).ProcessAsync(Status(), options);
        Assert.Equal(VideoOutcomeStatus.Failed, outcome.Status);
        Assert.True(outcome.IsConfiguration);
        Assert.True(outcome.StopsBatch);
    }

    [Fact]
    public async Task Processor_MkvToolNixMissingDuringExtraction_FailsOnlyThatVideo()
    {
        var processor = new VideoProcessor(() => throw new MkvToolNixNotFoundException("sin mkv"),
            _ => throw new InvalidOperationException("no debe crear cliente"), _jobs, _logs);
        var outcome = await processor.ProcessAsync(Status(englishExists: false), new VideoProcessingOptions());
        Assert.Equal(VideoOutcomeStatus.Failed, outcome.Status);
        Assert.Equal("sin mkv", outcome.Detail);
        Assert.False(outcome.StopsBatch);
    }

    [Fact]
    public async Task Processor_Cancelled_ReturnsCancelled()
    {
        WriteEnglish();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var outcome = await Processor(_ => new FakeTranslationClient()).ProcessAsync(Status(), new VideoProcessingOptions(), null, cts.Token);
        Assert.Equal(VideoOutcomeStatus.Cancelled, outcome.Status);
    }

    [Fact]
    public async Task Processor_ImageOnly_FailsWithReason()
    {
        var outcome = await Processor(_ => new FakeTranslationClient())
            .ProcessAsync(Status(selection: TrackSelection.Fail(SelectionFailure.ImageOnly)), new VideoProcessingOptions());
        Assert.Equal(VideoOutcomeStatus.Failed, outcome.Status);
        Assert.Equal("inglés solo en imagen (necesita OCR)", outcome.Detail);
    }

    [Fact]
    public void LocatePreferring_UsesGivenDirectoryFirst()
    {
        var dir = Path.Combine(_dir, "mkv");
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(dir, OperatingSystem.IsWindows() ? "mkvmerge.exe" : "mkvmerge"),
            Path.Combine(dir, OperatingSystem.IsWindows() ? "mkvextract.exe" : "mkvextract"),
        };
        var paths = MkvToolNixLocator.LocatePreferring(dir, exists.Contains);
        Assert.StartsWith(dir, paths.MkvMerge);
    }

    [Fact]
    public void DoctorAllOk_IgnoresOptionalChecks()
    {
        Assert.True(DoctorService.AllOk([new DoctorCheck(true, "a"), new DoctorCheck(false, "clave", Required: false)]));
        Assert.False(DoctorService.AllOk([new DoctorCheck(false, "a"), new DoctorCheck(true, "b")]));
        Assert.Equal("✅ MKVToolNix: x", new DoctorCheck(true, "MKVToolNix: x").ToString());
    }

    private sealed class ListProgress(List<VideoProgress> list) : IProgress<VideoProgress>
    {
        public void Report(VideoProgress value) => list.Add(value);
    }
}
