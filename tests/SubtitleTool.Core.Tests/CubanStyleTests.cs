using System.Text;
using SubtitleTool.Core.Review;
using SubtitleTool.Core.Srt;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Tests;

/// <summary>Versión cubana (<see cref="TranslationStyle.Cuban"/>): archivo aparte, prompt propio y sin tocar la traducción normal.</summary>
public sealed class CubanStyleTests : IDisposable
{
    private readonly string _dir;
    private readonly string _jobs;
    private readonly ReviewStore _reviews;

    public CubanStyleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "subtool-cu-tests-" + Guid.NewGuid().ToString("N"));
        _jobs = Path.Combine(_dir, "_jobs");
        Directory.CreateDirectory(_dir);
        _reviews = new ReviewStore(Path.Combine(_dir, "_reviews"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string WriteEnglish(int blocks, string name = "Movie.en.srt")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, SubtitleTranslatorTests.MakeSrt(blocks), new UTF8Encoding(false));
        return path;
    }

    private SubtitleTranslator Cuban(FakeTranslationClient fake, TranslationOptions? options = null) =>
        new(fake, (options ?? new TranslationOptions()) with { Style = TranslationStyle.Cuban }, _jobs, _reviews);

    [Theory]
    [InlineData("Movie.en.srt", "Movie.es-cu.srt")]
    [InlineData("Movie.en.sdh.srt", "Movie.es-cu.sdh.srt")]
    public void CubanPath_IsSeparateFromSpanish(string english, string expected)
    {
        var p = TranslationPaths.FromEnglish(Path.Combine(_dir, english));

        Assert.Equal(expected, Path.GetFileName(p.CubanPath));
        Assert.Equal(p.CubanPath, p.OutputPath(TranslationStyle.Cuban));
        Assert.Equal(p.SpanishPath, p.OutputPath(TranslationStyle.Neutral));
    }

    [Fact]
    public async Task Cuban_WritesEsCu_WithCubanPrompt_AndLeavesNormalFilesAlone()
    {
        var en = WriteEnglish(30);
        var paths = TranslationPaths.FromEnglish(en);
        File.WriteAllText(paths.SpanishPath, "normal", new UTF8Encoding(false)); // ya había traducción normal
        var fake = new FakeTranslationClient();

        var r = await Cuban(fake, new TranslationOptions { ReviewBeforeSave = true }).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Completed, r.Status);          // ni el .es.srt ni la revisión la frenan
        Assert.Equal(paths.CubanPath, r.OutputPath);
        Assert.All(fake.Requests, req => Assert.Equal(TranslationPrompts.CubanSystemPrompt, req.SystemPrompt));
        Assert.Equal("normal", File.ReadAllText(paths.SpanishPath));   // la traducción normal no se toca
        Assert.False(File.Exists(paths.PlainCopyPath));                // ni se crea la copia sin idioma
        Assert.False(_reviews.HasDraft(en));
        Assert.False(SrtValidator.Validate(SrtParser.ParseFile(en), SrtParser.ParseFile(paths.CubanPath)).HasErrors);
    }

    [Fact]
    public async Task Cuban_ExistingFile_IsNotOverwrittenWithoutForce()
    {
        var en = WriteEnglish(5);
        var paths = TranslationPaths.FromEnglish(en);
        File.WriteAllText(paths.CubanPath, "ya", new UTF8Encoding(false));
        var fake = new FakeTranslationClient();

        var skipped = await Cuban(fake).TranslateFileAsync(en);
        Assert.Equal(TranslationStatus.SkippedExists, skipped.Status);
        Assert.Empty(fake.Requests);

        var forced = await Cuban(fake, new TranslationOptions { Force = true }).TranslateFileAsync(en);
        Assert.Equal(TranslationStatus.Completed, forced.Status);
        Assert.NotEqual("ya", File.ReadAllText(paths.CubanPath));
    }

    [Fact]
    public async Task Cuban_DoesNotTouchPendingReviewDraft()
    {
        var en = WriteEnglish(10);
        var fake = new FakeTranslationClient();
        var normal = await new SubtitleTranslator(fake, new TranslationOptions { ReviewBeforeSave = true }, _jobs, _reviews).TranslateFileAsync(en);
        Assert.Equal(TranslationStatus.PendingReview, normal.Status);

        var r = await Cuban(fake).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Completed, r.Status);
        Assert.Equal(DraftState.Valid, _reviews.GetState(en)); // el borrador normal sigue ahí
    }

    [Fact]
    public async Task Cuban_ResumeStateIsSeparateFromNormal()
    {
        var en = WriteEnglish(20);
        var normalStore = new JobStore(_jobs, en);
        var cubanStore = new JobStore(_jobs, en, TranslationStyle.Cuban);
        Assert.NotEqual(normalStore.Directory, cubanStore.Directory);

        // Un trabajo normal a medias no se pierde por hacer la versión cubana.
        normalStore.SaveJob(new JobInfo { Source = en, Backend = TranslationBackends.ClaudeCode, Model = ModelPricing.DefaultModel, ChunkSize = 150, PromptVersion = TranslationPrompts.PromptVersion });
        await Cuban(new FakeTranslationClient()).TranslateFileAsync(en);

        Assert.True(normalStore.Exists);
        Assert.False(cubanStore.Exists); // completada: su estado se borra
    }
}
