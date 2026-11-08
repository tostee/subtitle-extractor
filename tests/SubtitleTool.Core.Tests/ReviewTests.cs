using SubtitleTool.Core.Library;
using SubtitleTool.Core.Review;
using SubtitleTool.Core.Srt;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.Core.Tests;

/// <summary>Fase 5: revisar la traducción antes de guardar el .es.srt.</summary>
public sealed class ReviewTests : IDisposable
{
    private readonly string _dir;
    private readonly ReviewStore _store;
    private readonly ReviewService _service;

    public ReviewTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "subtool-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new ReviewStore(Path.Combine(_dir, "_revisiones"));
        _service = new ReviewService(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string English(int blocks = 20)
    {
        var path = Path.Combine(_dir, "Peli.en.srt");
        File.WriteAllText(path, SubtitleTranslatorTests.MakeSrt(blocks));
        return path;
    }

    private SubtitleTranslator Translator(bool review, bool force = false, ITranslationClient? client = null) =>
        new(client ?? new FakePrefixTranslationClient(),
            new TranslationOptions { ChunkSize = 7, ReviewBeforeSave = review, Force = force },
            Path.Combine(_dir, "_jobs"), _store);

    [Fact]
    public async Task ReviewMode_WritesDraft_NotSpanish_AndDoesNotRetranslate()
    {
        var en = English();
        var paths = TranslationPaths.FromEnglish(en);

        var r = await Translator(review: true).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.PendingReview, r.Status);
        Assert.False(File.Exists(paths.SpanishPath));
        Assert.False(File.Exists(paths.PlainCopyPath));
        Assert.True(_store.HasDraft(en));
        Assert.Equal(_store.DraftPath(en), r.OutputPath);
        Assert.False(Directory.Exists(Path.Combine(_dir, "_jobs", JobStore.ComputeHash(en))));
        Assert.Contains("lista para revisar", VideoWorkflow.ResultDetail(r, claudeCode: true));

        // Con borrador pendiente no se vuelve a traducir (ni gasta límite), aunque ya no se pida revisar.
        var counting = new CountingClient();
        var again = await Translator(review: false, client: counting).TranslateFileAsync(en);
        Assert.Equal(TranslationStatus.PendingReview, again.Status);
        Assert.Equal(0, counting.Calls);

        // --force descarta el borrador y traduce de nuevo.
        var forced = await Translator(review: false, force: true).TranslateFileAsync(en);
        Assert.Equal(TranslationStatus.Completed, forced.Status);
        Assert.False(_store.HasDraft(en));
        Assert.True(File.Exists(paths.SpanishPath));
    }

    [Fact]
    public async Task Draft_EditSaveDraftReopen_ThenSave_WritesSpanishAndPlainCopy()
    {
        var en = English();
        var paths = TranslationPaths.FromEnglish(en);
        await Translator(review: true).TranslateFileAsync(en);

        var s = await _service.OpenAsync(en);
        Assert.Equal(ReviewSource.Draft, s.Source);
        Assert.All(s.Blocks.Where(b => !b.IsEmptyOriginal), b => Assert.StartsWith("ES: ", b.Spanish));
        s.Blocks[2].Spanish = "Texto corregido\na mano";
        Assert.Equal([3], s.EditedIds);
        await _service.SaveDraftAsync(s);

        var reopened = await _service.OpenAsync(en);
        Assert.Equal("Texto corregido\na mano", reopened.Blocks[2].Spanish);
        Assert.True(reopened.Blocks[2].IsEdited); // se recuerda entre sesiones
        Assert.False(reopened.Blocks[3].IsEdited);

        var result = await _service.SaveAsync(reopened);
        Assert.Equal(paths.SpanishPath, result.SpanishPath);
        Assert.Equal(PlainCopyStatus.Created, result.PlainCopy);
        Assert.False(_store.HasDraft(en));
        var saved = SrtParser.Parse(File.ReadAllText(paths.SpanishPath));
        Assert.Equal(["Texto corregido", "a mano"], saved.Blocks[2].Lines);
        Assert.Equal(SrtParser.Parse(File.ReadAllText(en)).Blocks[2].TimingLine, saved.Blocks[2].TimingLine);
    }

    [Fact]
    public async Task Save_RefusesEmptyBlock_AndNeverOverwritesFromDraft()
    {
        var en = English();
        var paths = TranslationPaths.FromEnglish(en);
        await Translator(review: true).TranslateFileAsync(en);

        var s = await _service.OpenAsync(en);
        s.Blocks[0].Spanish = "   ";
        var empty = await Assert.ThrowsAsync<ReviewException>(() => _service.SaveAsync(s));
        Assert.Contains("1 error", empty.Message);
        Assert.False(File.Exists(paths.SpanishPath));

        s.Blocks[0].Spanish = "Hola";
        File.WriteAllText(paths.SpanishPath, "MÍO");
        var exists = await Assert.ThrowsAsync<ReviewException>(() => _service.SaveAsync(s));
        Assert.Contains("no se sobrescribe", exists.Message);
        Assert.Equal("MÍO", File.ReadAllText(paths.SpanishPath));
        Assert.True(_store.HasDraft(en)); // el borrador sigue ahí
    }

    [Fact]
    public async Task SavedFile_CanBeReviewed_AndKeepsBackup()
    {
        var en = English();
        var paths = TranslationPaths.FromEnglish(en);
        await Translator(review: false).TranslateFileAsync(en);
        var before = File.ReadAllText(paths.SpanishPath);

        var s = await _service.OpenAsync(en);
        Assert.Equal(ReviewSource.SavedFile, s.Source);
        await Assert.ThrowsAsync<ReviewException>(() => _service.SaveDraftAsync(s));
        s.Blocks[0].Spanish = "Corregido";
        var r = await _service.SaveAsync(s);

        Assert.Equal(before, File.ReadAllText(r.BackupPath!));
        Assert.Equal(["Corregido"], SrtParser.Parse(File.ReadAllText(paths.SpanishPath)).Blocks[0].Lines);
    }

    [Fact]
    public async Task Open_NothingToReview_OrDifferentBlockCount_Explains()
    {
        var en = English();
        var none = await Assert.ThrowsAsync<ReviewException>(() => _service.OpenAsync(en));
        Assert.Contains("No hay traducción", none.Message);

        File.WriteAllText(TranslationPaths.FromEnglish(en).SpanishPath, SubtitleTranslatorTests.MakeSrt(5));
        var mismatch = await Assert.ThrowsAsync<ReviewException>(() => _service.OpenAsync(en));
        Assert.Contains("lado a lado", mismatch.Message);
    }

    [Fact]
    public async Task Discard_RemovesDraft_SoTheVideoCanBeTranslatedAgain()
    {
        var en = English();
        await Translator(review: true).TranslateFileAsync(en);
        _service.Discard(en);
        Assert.False(_store.HasDraft(en));
        Assert.Equal(TranslationStatus.Completed, (await Translator(review: false).TranslateFileAsync(en)).Status);
    }

    [Fact]
    public async Task Scanner_MarksPendingReview_AndPlanSkipsTranslation()
    {
        var video = Path.Combine(_dir, "Peli.mkv");
        File.WriteAllText(video, "");
        var en = English();
        await Translator(review: true).TranslateFileAsync(en);

        var inspector = new FixedInspector();
        var st = await new LibraryScanner(inspector, _store).ScanVideoAsync(video);
        Assert.True(st.ReviewPending);
        var plan = VideoWorkflow.Plan(st, force: false);
        Assert.Equal(VideoPlanKind.PendingReview, plan.Kind);

        var outcome = await new VideoProcessor(() => throw new InvalidOperationException("no debe extraer"),
                _ => throw new InvalidOperationException("no debe traducir"), Path.Combine(_dir, "_jobs"), Path.Combine(_dir, "_logs"))
            .ProcessAsync(st, new VideoProcessingOptions());
        Assert.Equal(VideoOutcomeStatus.PendingReview, outcome.Status);
    }

    private sealed class FixedInspector : Media.IVideoInspector
    {
        public Task<Mkv.MkvInfo> InspectAsync(string videoPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Mkv.MkvInfo(videoPath,
            [
                new Mkv.MkvTrack { Id = 3, Type = "subtitles", Codec = "SubRip/SRT", CodecId = "S_TEXT/UTF8", Language = "eng" },
            ]));
    }

    private sealed class CountingClient : ITranslationClient
    {
        private readonly FakePrefixTranslationClient _inner = new();
        public int Calls { get; private set; }

        public Task<ChunkResponse> TranslateChunkAsync(ChunkRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return _inner.TranslateChunkAsync(request, cancellationToken);
        }

        public Task<GlossaryResponse> BuildGlossaryAsync(GlossaryRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return _inner.BuildGlossaryAsync(request, cancellationToken);
        }

        public Task<long> CountTokensAsync(string? systemPrompt, string userMessage, CancellationToken cancellationToken = default) =>
            _inner.CountTokensAsync(systemPrompt, userMessage, cancellationToken);
    }
}
