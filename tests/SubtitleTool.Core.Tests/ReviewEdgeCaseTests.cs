using SubtitleTool.Core.Library;
using SubtitleTool.Core.Review;
using SubtitleTool.Core.Srt;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.Core.Tests;

/// <summary>Casos límite de la revisión que señaló la revisión de código (borradores atascados, copias, tiempos…).</summary>
public sealed class ReviewEdgeCaseTests : IDisposable
{
    private readonly string _dir;
    private readonly ReviewStore _store;

    public ReviewEdgeCaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "subtool-review-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new ReviewStore(Path.Combine(_dir, "_revisiones"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string English(int blocks = 12)
    {
        var path = Path.Combine(_dir, "Peli.en.srt");
        File.WriteAllText(path, SubtitleTranslatorTests.MakeSrt(blocks));
        return path;
    }

    private Task<TranslationResult> Translate(string en, bool review, bool force = false, ITranslationClient? client = null) =>
        new SubtitleTranslator(client ?? new FakePrefixTranslationClient(),
            new TranslationOptions { ChunkSize = 5, ReviewBeforeSave = review, Force = force }, Path.Combine(_dir, "_jobs"), _store)
            .TranslateFileAsync(en);

    [Fact]
    public async Task StaleDraft_EnglishChanged_IsNotPending_CannotOpen_AndIsReplacedOnNextTranslation()
    {
        var en = English();
        await Translate(en, review: true);
        Assert.Equal(DraftState.Valid, _store.GetState(en));

        File.WriteAllText(en, SubtitleTranslatorTests.MakeSrt(8)); // se volvió a extraer: otro inglés
        Assert.Equal(DraftState.Stale, _store.GetState(en));
        Assert.False(_store.HasValidDraft(en));

        var ex = await Assert.ThrowsAsync<ReviewException>(() => new ReviewService(_store).OpenAsync(en));
        Assert.True(ex.IsStaleDraft);

        // Procesar de nuevo no se queda atascado: descarta el borrador viejo (con aviso) y traduce.
        var r = await Translate(en, review: true);
        Assert.Equal(TranslationStatus.PendingReview, r.Status);
        Assert.Contains(r.Warnings, w => w.Contains("otra versión"));
        Assert.Equal(DraftState.Valid, _store.GetState(en));
        Assert.Equal(8, (await new ReviewService(_store).OpenAsync(en)).Blocks.Count);
    }

    [Fact]
    public async Task StaleDraft_EnglishDeleted_PlanReExtractsInsteadOfWaitingForReview()
    {
        var video = Path.Combine(_dir, "Peli.mkv");
        File.WriteAllText(video, "");
        var en = English();
        await Translate(en, review: true);
        File.Delete(en);

        var st = await new LibraryScanner(new SingleTrackInspector(), _store).ScanVideoAsync(video);
        Assert.False(st.ReviewPending);
        var plan = VideoWorkflow.Plan(st, force: false);
        Assert.Equal(VideoPlanKind.Process, plan.Kind);
        Assert.True(plan.NeedsExtraction);
    }

    [Fact]
    public async Task ForceReview_OverExistingSpanish_CanBeSaved_WithBackup()
    {
        var en = English();
        var paths = TranslationPaths.FromEnglish(en);
        File.WriteAllText(paths.SpanishPath, SubtitleTranslatorTests.MakeSrt(12).Replace("Line", "VIEJO"));
        var original = File.ReadAllText(paths.SpanishPath);

        Assert.Equal(TranslationStatus.PendingReview, (await Translate(en, review: true, force: true)).Status);
        Assert.Equal(original, File.ReadAllText(paths.SpanishPath)); // todavía no se toca

        var service = new ReviewService(_store);
        var s = await service.OpenAsync(en);
        Assert.Equal(ReviewSource.Draft, s.Source);
        Assert.True(s.OverwritesExisting);
        var r = await service.SaveAsync(s);

        Assert.Equal(original, File.ReadAllText(r.BackupPath!));
        Assert.Contains("ES: ", File.ReadAllText(paths.SpanishPath));
        Assert.False(_store.HasDraft(en));
    }

    [Fact]
    public async Task SavedFile_TwoSaves_KeepBothBackups_AndOwnTimings()
    {
        var en = English();
        var paths = TranslationPaths.FromEnglish(en);
        await Translate(en, review: false);
        // El .es.srt se resincronizó a mano: sus tiempos ya no son los del inglés.
        var resynced = SrtParser.Parse(File.ReadAllText(paths.SpanishPath));
        foreach (var b in resynced.Blocks)
        {
            b.Start += TimeSpan.FromSeconds(1);
            b.End += TimeSpan.FromSeconds(1);
            b.TimingLine = $"{b.Start:hh\\:mm\\:ss\\,fff} --> {b.End:hh\\:mm\\:ss\\,fff}";
        }
        SrtWriter.WriteFile(paths.SpanishPath, resynced);
        var original = File.ReadAllText(paths.SpanishPath);

        var clock = new DateTime(2026, 11, 8, 10, 0, 0);
        var service = new ReviewService(_store, () => clock);
        var s1 = await service.OpenAsync(en);
        Assert.False(s1.Validate().HasErrors); // tiempos distintos al inglés: no impiden guardar
        s1.Blocks[0].Spanish = "Primera corrección";
        var r1 = await service.SaveAsync(s1);

        var s2 = await service.OpenAsync(en);
        s2.Blocks[1].Spanish = "Segunda corrección";
        var r2 = await service.SaveAsync(s2); // misma hora: no pisa la primera copia

        Assert.NotEqual(r1.BackupPath, r2.BackupPath);
        Assert.Equal(original, File.ReadAllText(r1.BackupPath!));
        var saved = SrtParser.Parse(File.ReadAllText(paths.SpanishPath));
        Assert.Equal(resynced.Blocks[5].TimingLine, saved.Blocks[5].TimingLine);
        Assert.Equal(["Primera corrección"], saved.Blocks[0].Lines);
        Assert.Equal(["Segunda corrección"], saved.Blocks[1].Lines);
    }

    [Fact]
    public async Task PlainCopyFailure_IsAWarning_AndTheDraftIsStillCleanedUp()
    {
        var en = English();
        var paths = TranslationPaths.FromEnglish(en);
        await Translate(en, review: true);
        Directory.CreateDirectory(paths.PlainCopyPath + ".tmp"); // la copia sin idioma no se puede escribir

        var service = new ReviewService(_store);
        var r = await service.SaveAsync(await service.OpenAsync(en));

        Assert.Equal(PlainCopyStatus.Failed, r.PlainCopy);
        Assert.True(File.Exists(paths.SpanishPath));
        Assert.False(_store.HasDraft(en));
    }

    [Fact]
    public async Task ForceRetranslation_Cancelled_KeepsTheReviewedDraft()
    {
        var en = English();
        await Translate(en, review: true);
        var service = new ReviewService(_store);
        var s = await service.OpenAsync(en);
        s.Blocks[0].Spanish = "Mi corrección";
        await service.SaveDraftAsync(s);

        await Assert.ThrowsAsync<OperationCanceledException>(() => Translate(en, review: true, force: true, client: new CancellingClient()));

        Assert.Equal("Mi corrección", (await service.OpenAsync(en)).Blocks[0].Spanish);
    }

    private sealed class SingleTrackInspector : Media.IVideoInspector
    {
        public Task<Mkv.MkvInfo> InspectAsync(string videoPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Mkv.MkvInfo(videoPath,
                [new Mkv.MkvTrack { Id = 3, Type = "subtitles", Codec = "SubRip/SRT", CodecId = "S_TEXT/UTF8", Language = "eng" }]));
    }

    private sealed class CancellingClient : ITranslationClient
    {
        public Task<ChunkResponse> TranslateChunkAsync(ChunkRequest request, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException();

        public Task<GlossaryResponse> BuildGlossaryAsync(GlossaryRequest request, CancellationToken cancellationToken = default) =>
            new FakePrefixTranslationClient().BuildGlossaryAsync(request, cancellationToken);

        public Task<long> CountTokensAsync(string? systemPrompt, string userMessage, CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);
    }
}
