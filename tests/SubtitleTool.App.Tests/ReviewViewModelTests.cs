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
    public async Task Close_WithUnsavedChanges_AsksToSave()
    {
        var (_, english) = await VideoWithDraftAsync();
        var vm = await OpenAsync(english);
        Assert.True(vm.CanClose());
        Assert.Empty(_h.Dialogs.AskSaves);

        vm.Rows[0].Spanish = "Hola.";
        _h.Dialogs.AskSaveResult = null;   // Cancelar: se queda en la ventana
        Assert.False(vm.CanClose());
        Assert.Contains("borrador", Assert.Single(_h.Dialogs.AskSaves));
        _h.Dialogs.AskSaveResult = false;  // No guardar: cierra y pierde los cambios
        Assert.True(vm.CanClose());
        Assert.Equal(ReviewOutcome.None, vm.Outcome);
    }

    [Fact]
    public async Task Close_AnswerSave_SavesDraft_ThenClosesItself()
    {
        var (_, english) = await VideoWithDraftAsync();
        var vm = await OpenAsync(english);
        var closed = new TaskCompletionSource();
        vm.CloseRequested += () => closed.TrySetResult();
        vm.Rows[0].Spanish = "Hola.";

        _h.Dialogs.AskSaveResult = true;
        Assert.False(vm.CanClose()); // no se cierra todavía: primero se guarda
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ReviewOutcome.DraftSaved, vm.Outcome);
        Assert.False(vm.IsDirty);
        Assert.Contains("Hola.", File.ReadAllText(new ReviewStore(_h.ReviewRoot).DraftPath(english)));
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
        Assert.Equal(VideoItemViewModel.ExistsText, row.SpanishText);
        Assert.Equal(StatusKind.Success, row.SpanishStatus);
        Assert.Contains(main.LogLines, l => l.Text.Contains("revisada y guardada (1 bloque(s) editados)"));
    }

    [Fact]
    public async Task QueueEndDialogs_WaitUntilReviewWindowCloses()
    {
        var (video, _) = await VideoWithDraftAsync();
        var other = Path.Combine(_dir, "Otra.mkv");
        _h.Library.Folders[_dir] = [video, other];
        _h.Processing.Handler = (_, _, _) => Task.FromResult(new VideoOutcome
        {
            Status = VideoOutcomeStatus.Failed, Detail = "límite de uso alcanzado (reanudable)", IsUsageLimit = true,
        });
        var main = _h.CreateMain();
        await main.AddPathsAsync([_dir]);
        var (row, otherRow) = (main.Videos[0], main.Videos[1]);

        var warningsDuringReview = -1;
        _h.Dialogs.OnReview = async r =>
        {
            // La cola termina (límite de uso) con la revisión abierta: el aviso no debe salir encima.
            await main.ProcessOneCommand.ExecuteAsync(otherRow);
            warningsDuringReview = _h.Dialogs.Warnings.Count;
            await r.SaveDraftCommand.ExecuteAsync(null);
        };
        await Task.Run(() => main.OpenRowCommand.ExecuteAsync(row));

        Assert.Equal(0, warningsDuringReview);
        Assert.Contains(main.LogLines, l => l.Text.Contains("se mostrará al cerrar la revisión"));
        Assert.Single(_h.Dialogs.Warnings); // se muestra al cerrar la revisión

        // Sin revisión abierta, el aviso sale en el momento, como siempre.
        await main.ProcessOneCommand.ExecuteAsync(otherRow);
        Assert.Equal(2, _h.Dialogs.Warnings.Count);
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
    public async Task Issues_HaveAKindAndAPlainText()
    {
        var (_, english) = await VideoWithDraftAsync();
        var vm = await OpenAsync(english);

        vm.Rows[0].Spanish = "";
        var issue = Assert.Single(vm.Rows[0].Issues);
        Assert.Equal(StatusKind.Error, issue.Kind);
        Assert.Equal("vacío", issue.Text);
        Assert.Equal("error", issue.Label);
        Assert.Equal("Error: vacío", vm.Rows[0].IssuesText);   // texto para Narrador (HelpText), sin emoji

        vm.Rows[1].Spanish = "El mar.";
        Assert.Equal(StatusKind.Warning, Assert.Single(vm.Rows[1].Issues).Kind);
        Assert.StartsWith("Aviso: ", vm.Rows[1].IssuesText);
    }

    [Fact]
    public async Task StatusKind_FollowsTheStatusText()
    {
        var (_, english) = await VideoWithDraftAsync();
        var vm = await OpenAsync(english);

        vm.NextIssueCommand.Execute(null);
        Assert.Equal(StatusKind.Info, vm.StatusKind);
        vm.Rows[0].Spanish = " ";
        await vm.SaveCommand.ExecuteAsync(null);                 // bloque vacío: no se puede guardar
        Assert.Equal(StatusKind.Error, vm.StatusKind);
        Assert.DoesNotContain("❌", vm.StatusText);
        vm.StatusText = "otra cosa";                             // texto sin tipo: sin icono
        Assert.Equal(StatusKind.None, vm.StatusKind);
    }

    /// <summary>Servicio de revisión que falla de forma inesperada al guardar el borrador.</summary>
    private sealed class ExplodingDraftService(Services.IReviewService inner) : Services.IReviewService
    {
        public bool HasDraft(string? englishPath) => inner.HasDraft(englishPath);
        public Task<ReviewSession> OpenAsync(string englishPath, CancellationToken ct = default) => inner.OpenAsync(englishPath, ct);
        public Task SaveDraftAsync(ReviewSession session, CancellationToken ct = default) =>
            Task.FromException(new InvalidOperationException("fallo inesperado (prueba)"));
        public Task<ReviewSaveResult> SaveAsync(ReviewSession session, CancellationToken ct = default) => inner.SaveAsync(session, ct);
        public void Discard(string englishPath) => inner.Discard(englishPath);
    }

    [Fact]
    public async Task Close_AnswerSave_UnexpectedError_IsShown_AndTheWindowStaysOpen()
    {
        var (_, english) = await VideoWithDraftAsync();
        var vm = new ReviewViewModel(await _h.Reviews.OpenAsync(english), new ExplodingDraftService(_h.Reviews), _h.Dialogs);
        var closed = false;
        vm.CloseRequested += () => closed = true;
        vm.Rows[0].Spanish = "Hola.";

        await vm.SaveBeforeCloseAsync();

        Assert.False(closed);
        Assert.False(vm.IsBusy);
        Assert.True(vm.IsDirty);
        Assert.Equal(StatusKind.Error, vm.StatusKind);
        Assert.Contains("fallo inesperado", Assert.Single(_h.Dialogs.Errors));
    }

    [Fact]
    public async Task Close_IsBlockedWhileSaving()
    {
        var (_, english) = await VideoWithDraftAsync();
        var vm = await OpenAsync(english);
        vm.IsBusy = true;
        Assert.False(vm.CanClose());
        Assert.Equal(ReviewViewModel.BusyCloseMessage, vm.StatusText); // se explica por qué no se cierra
        Assert.Empty(_h.Dialogs.AskSaves);
    }

    [Fact]
    public async Task NextIssue_WithoutIssues_SaysSo_AndClearsTheMessageWhenItFindsOne()
    {
        var (_, english) = await VideoWithDraftAsync();
        var vm = await OpenAsync(english);
        var focused = new List<int>();
        vm.FocusRowRequested += r => focused.Add(r.Number);

        vm.NextIssueCommand.Execute(null);
        Assert.Equal(ReviewViewModel.NoIssuesMessage, vm.StatusText);
        Assert.Null(vm.SelectedRow);
        Assert.Empty(focused);

        vm.Rows[1].Spanish = "El mar.";   // aviso: se pierde la cursiva
        vm.NextIssueCommand.Execute(null);
        Assert.Equal(2, vm.SelectedRow!.Number);
        Assert.Equal([2], focused);        // la vista lleva el cursor a ese bloque
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public async Task Save_ShowsProgressInStatus()
    {
        var (_, english) = await VideoWithDraftAsync();
        var vm = await OpenAsync(english);
        var statuses = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReviewViewModel.StatusText))
                statuses.Add(vm.StatusText);
        };

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Guardando…", statuses[0]);
        Assert.StartsWith("Guardado:", vm.StatusText);
    }

    [Fact]
    public async Task Filter_ReplacesTheVisibleRowsInOneStep()
    {
        var (_, english) = await VideoWithDraftAsync();
        var vm = await OpenAsync(english);
        var rowsChanges = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReviewViewModel.Rows))
                rowsChanges++;
        };

        vm.SearchText = "sea";

        Assert.Equal(1, rowsChanges);      // una sola notificación aunque haya miles de bloques
        Assert.Equal(2, Assert.Single(vm.Rows).Number);
        Assert.Contains("mostrando 1", vm.SummaryText);
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
        Assert.Contains(main.LogLines, l => l.Text.Contains("el inglés había cambiado"));
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
