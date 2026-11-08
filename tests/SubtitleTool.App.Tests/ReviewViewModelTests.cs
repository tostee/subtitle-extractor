using SubtitleTool.App.ViewModels;
using SubtitleTool.Core.Review;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.App.Tests;

public sealed class ReviewViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subtool-review-vm-" + Guid.NewGuid().ToString("N"));
    private readonly Harness _h = new();

    public ReviewViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in new[] { _dir, _h.ReviewRoot })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private const string Srt = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello.\r\n\r\n" +
                               "2\r\n00:00:03,000 --> 00:00:04,000\r\n<i>The sea.</i>\r\n\r\n" +
                               "3\r\n00:00:05,000 --> 00:00:06,000\r\n- Where?\r\n- Home.\r\n";

    /// <summary>Video falso con su .en.srt y un borrador traducido (traductor falso «ES: »).</summary>
    private async Task<(string Video, string English)> VideoWithDraftAsync()
    {
        var video = Path.Combine(_dir, "Peli.mkv");
        var english = Path.Combine(_dir, "Peli.en.srt");
        File.WriteAllText(english, Srt);
        var store = new ReviewStore(_h.ReviewRoot);
        var r = await new SubtitleTranslator(new FakePrefixTranslationClient(), new TranslationOptions { ReviewBeforeSave = true },
            Path.Combine(_dir, "_jobs"), store).TranslateFileAsync(english);
        Assert.Equal(TranslationStatus.PendingReview, r.Status);
        // Como el escaneo real: refleja lo que hay en disco (borrador y .es.srt).
        _h.Library.Statuses[video] = () =>
            TestVideos.Translatable(video, spanishExists: File.Exists(Path.Combine(_dir, "Peli.es.srt"))) with { ReviewPending = store.HasDraft(english) };
        return (video, english);
    }

    private async Task<ReviewViewModel> OpenAsync(string english) =>
        new(await _h.Reviews.OpenAsync(english), _h.Reviews, _h.Dialogs);

    [Fact]
    public async Task Rows_ShowEnglishAndSpanish_WithPerBlockWarnings()
    {
        var (_, english) = await VideoWithDraftAsync();
        var vm = await OpenAsync(english);

        Assert.True(vm.IsDraft);
        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal("Hello.", vm.Rows[0].English);
        Assert.Equal("ES: Hello.", vm.Rows[0].Spanish);
        Assert.Equal("00:00:01,000 → 00:00:02,000", vm.Rows[0].Timing);
        Assert.All(vm.Rows, r => Assert.Equal("", r.IssuesText));
        Assert.False(vm.IsDirty);

        // Los avisos se recalculan al escribir.
        vm.Rows[1].Spanish = "El mar.";                       // se pierde la cursiva
        Assert.Contains("etiquetas", vm.Rows[1].IssuesText);
        vm.Rows[2].Spanish = "¿Adónde?\nA casa.";            // se pierden los guiones de diálogo
        Assert.Contains("guiones", vm.Rows[2].IssuesText);
        vm.Rows[0].Spanish = "Una línea demasiado larga para un subtítulo legible";
        Assert.Contains("42 caracteres", vm.Rows[0].IssuesText);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task Editing_MarksDirty_UpdatesWarnings_AndFilters()
    {
        var (_, english) = await VideoWithDraftAsync();
        var vm = await OpenAsync(english);

        vm.Rows[1].Spanish = "<i>El mar.</i>";
        Assert.True(vm.IsDirty);
        Assert.True(vm.Rows[1].IsEdited);
        Assert.False(vm.Rows[1].HasWarning);

        vm.Rows[0].Spanish = "";
        Assert.True(vm.Rows[0].HasError);
        vm.Rows[2].Spanish = "- ¿Adónde?\n- A casa.\n- Ya.";  // 3 líneas → aviso
        Assert.Contains("más de 2 líneas", vm.Rows[2].IssuesText);
        Assert.Contains("1 con errores", vm.SummaryText);

        vm.OnlyEdited = true;
        Assert.Equal(3, vm.Rows.Count);
        vm.OnlyEdited = false;
        vm.OnlyIssues = true;
        Assert.Equal(["1", "3"], vm.Rows.Select(r => r.Number.ToString()));
        vm.OnlyIssues = false;
        vm.SearchText = "HOME";   // sin distinguir mayúsculas
        Assert.Equal(3, Assert.Single(vm.Rows).Number);
        vm.SearchText = "mar";    // busca también en español
        Assert.Equal(2, Assert.Single(vm.Rows).Number);
        vm.SearchText = "";

        vm.NextIssueCommand.Execute(null);
        Assert.Equal(1, vm.SelectedRow!.Number);
        vm.NextIssueCommand.Execute(null);
        Assert.Equal(3, vm.SelectedRow!.Number);
    }

    [Fact]
    public async Task Save_WithEmptyBlock_Warns_ThenSavesAndCloses()
    {
        var (_, english) = await VideoWithDraftAsync();
        var vm = await OpenAsync(english);
        var closed = false;
        vm.CloseRequested += () => closed = true;

        vm.Rows[0].Spanish = " ";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(closed);
        Assert.Contains("No se puede guardar", Assert.Single(_h.Dialogs.Warnings));

        vm.Rows[0].Spanish = "Hola.";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(closed);
        Assert.Equal(ReviewOutcome.Saved, vm.Outcome);
        Assert.True(File.Exists(Path.Combine(_dir, "Peli.es.srt")));
        Assert.False(_h.Reviews.HasDraft(english));
    }

    [Fact]
    public async Task Close_WithUnsavedChanges_Asks()
    {
        var (_, english) = await VideoWithDraftAsync();
        var vm = await OpenAsync(english);
        Assert.True(vm.CanClose());

        vm.Rows[0].Spanish = "Hola.";
        _h.Dialogs.ConfirmResult = false;
        Assert.False(vm.CanClose());
        _h.Dialogs.ConfirmResult = true;
        Assert.True(vm.CanClose());
    }

    [Fact]
    public async Task MainWindow_DoubleClickPendingRow_OpensReview_AndUpdatesRow()
    {
        var (video, english) = await VideoWithDraftAsync();
        _h.Library.Folders[_dir] = [video];
        var main = _h.CreateMain();
        await main.AddPathsAsync([_dir]);
        var row = Assert.Single(main.Videos);
        Assert.True(row.IsReviewPending);
        Assert.Equal(VideoItemViewModel.ReviewText, row.SpanishText);
        Assert.Equal(VideoPlanKind.PendingReview, VideoWorkflow.Plan(row.Status!, false).Kind);

        _h.Dialogs.OnReview = r =>
        {
            r.Rows[0].Spanish = "Hola.";
            return r.SaveDraftCommand.ExecuteAsync(null);
        };
        // Sin contexto de sincronización: la ventana falsa espera (como la real, que es modal) sin bloquear.
        await Task.Run(() => main.OpenRowCommand.ExecuteAsync(row));
        Assert.NotNull(_h.Dialogs.ReviewShown);
        Assert.Contains("a medias", row.Detail);
        Assert.Contains("Hola.", File.ReadAllText(new ReviewStore(_h.ReviewRoot).DraftPath(english)));

        _h.Dialogs.OnReview = r => r.SaveCommand.ExecuteAsync(null);
        await Task.Run(() => main.ReviewCommand.ExecuteAsync(row));
        Assert.Equal("✅ existe", row.SpanishText);
        Assert.Contains(main.LogLines, l => l.Contains("revisada y guardada (1 bloque(s) editados)"));
    }

    [Fact]
    public async Task Review_WithoutTranslation_ExplainsInsteadOfOpening()
    {
        var video = Path.Combine(_dir, "Sola.mkv");
        File.WriteAllText(Path.Combine(_dir, "Sola.en.srt"), Srt);
        _h.Library.Folders[_dir] = [video];
        var main = _h.CreateMain();
        await main.AddPathsAsync([_dir]);

        await main.ReviewCommand.ExecuteAsync(main.Videos[0]);
        Assert.Null(_h.Dialogs.ReviewShown);
        Assert.Contains("No hay traducción", Assert.Single(_h.Dialogs.Infos));
    }

    [Fact]
    public async Task Close_IsBlockedWhileSaving()
    {
        var (_, english) = await VideoWithDraftAsync();
        var vm = await OpenAsync(english);
        vm.IsBusy = true;
        Assert.False(vm.CanClose());
    }

    [Fact]
    public async Task StaleDraft_OffersDiscard_FromTheMainWindow()
    {
        var (video, english) = await VideoWithDraftAsync();
        File.WriteAllText(english, Srt.Replace("Hello.", "Hello there.")); // el inglés cambió
        _h.Library.Folders[_dir] = [video];
        _h.Library.Statuses[video] = () => TestVideos.Translatable(video) with
        {
            EnglishPath = english, ReviewPending = new ReviewStore(_h.ReviewRoot).HasValidDraft(english),
        };
        var main = _h.CreateMain();
        await main.AddPathsAsync([_dir]);
        var row = Assert.Single(main.Videos);
        Assert.False(row.IsReviewPending);

        _h.Dialogs.ConfirmResult = true;
        await main.ReviewCommand.ExecuteAsync(row);

        Assert.Null(_h.Dialogs.ReviewShown);
        Assert.False(_h.Reviews.HasDraft(english));
        Assert.Contains(main.LogLines, l => l.Contains("el inglés había cambiado"));
    }

    [Fact]
    public void CanReview_OnlyWhenTheMatchingSpanishExists()
    {
        var video = Path.Combine(_dir, "Sdh.mkv");
        var row = new VideoItemViewModel(video);
        row.ApplyStatus(TestVideos.Translatable(video) with { SpanishExisting = [Path.Combine(_dir, "Sdh.es.sdh.srt")] });
        Assert.False(row.CanReview); // la pista es normal (.en.srt) y solo hay .es.sdh.srt
        row.ApplyStatus(TestVideos.Translatable(video, spanishExists: true));
        Assert.True(row.CanReview);
    }

    [Fact]
    public void Settings_ReviewBeforeSave_RoundTripsToProcessingOptions()
    {
        var vm = new SettingsViewModel(new Models.AppSettings(), _h.Library, _h.Doctor, _h.ApiKeys, _h.Dialogs);
        Assert.False(vm.ReviewBeforeSave);
        vm.ReviewBeforeSave = true;
        Assert.True(vm.TrySave());
        Assert.True(vm.Result.ToProcessingOptions().Translation.ReviewBeforeSave);
    }
}
