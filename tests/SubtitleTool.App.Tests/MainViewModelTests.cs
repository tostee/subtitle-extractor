using SubtitleTool.App.Models;
using SubtitleTool.App.ViewModels;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Updates;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.App.Tests;

public sealed class MainViewModelTests
{
    private const string Folder = @"X:\Pelis";

    [Fact]
    public async Task AddPaths_ScansEachVideo_AndShowsTrackAndStates()
    {
        var h = new Harness();
        var videos = h.AddFolder(Folder, "a.mkv", "b.mkv", "c.mkv");
        h.Library.Statuses[videos[1]] = () => TestVideos.ImageOnly(videos[1]);
        h.Library.Statuses[videos[2]] = () => TestVideos.Translatable(videos[2], englishExists: false, spanishExists: true);
        var vm = h.CreateMain();

        await vm.AddPathsAsync([Folder]);

        Assert.Equal(3, vm.Videos.Count);
        Assert.True(vm.HasVideos);
        Assert.False(vm.IsScanning);
        var a = vm.Videos[0];
        Assert.Equal("a.mkv", a.Name);
        Assert.Equal("3 (normal)", a.TrackText);
        Assert.Equal("existe", a.EnglishText);
        Assert.Equal(StatusKind.Success, a.EnglishStatus);
        Assert.Equal("pendiente", a.SpanishText);
        Assert.Equal(RowState.Ready, a.State);
        Assert.Equal("solo imagen — necesita OCR", vm.Videos[1].TrackText);
        Assert.Equal("no disponible", vm.Videos[1].EnglishText);
        Assert.Equal(StatusKind.Error, vm.Videos[1].EnglishStatus);
        Assert.Equal("pendiente", vm.Videos[2].EnglishText);
        Assert.Equal("existe", vm.Videos[2].SpanishText);
    }

    [Fact]
    public async Task AddPaths_IgnoresDuplicates_AndLogsUnreadablePaths()
    {
        var h = new Harness();
        h.AddFolder(Folder, "a.mkv");
        var vm = h.CreateMain();

        await vm.AddPathsAsync([Folder]);
        await vm.AddPathsAsync([Folder, @"X:\NoExiste"]);

        Assert.Single(vm.Videos);
        Assert.Contains(vm.LogLines, l => l.Text.Contains("No se pudo leer") && l.Text.Contains("NoExiste"));
    }

    [Fact]
    public async Task AddPaths_WithoutMkvToolNix_MarksMkvRowsAndWarnsOnce()
    {
        var h = new Harness();
        h.AddFolder(Folder, "a.mkv", "b.mkv", "c.mp4");
        h.Library.MkvMissing = true;
        var vm = h.CreateMain();

        await vm.AddPathsAsync([Folder]);

        Assert.Equal("error al inspeccionar", vm.Videos[0].TrackText);
        Assert.Equal("error al inspeccionar", vm.Videos[1].TrackText);
        Assert.NotEqual("error al inspeccionar", vm.Videos[2].TrackText);
        Assert.Contains("Los .mp4 se procesan igualmente", Assert.Single(h.Dialogs.Errors));
    }

    [Fact]
    public async Task ProcessAll_WithoutMkvToolNix_StillProcessesMp4()
    {
        var h = new Harness();
        h.AddFolder(Folder, "a.mkv", "b.mp4", "c.mkv", "d.mp4");
        h.Library.MkvMissing = true;
        h.Processing.Handler = (st, _, _) => Task.FromResult(st.Error is not null
            ? new VideoOutcome { Status = VideoOutcomeStatus.Failed, Detail = "error al inspeccionar: " + st.Error }
            : new VideoOutcome { Status = VideoOutcomeStatus.Completed, Detail = "ok" });
        var vm = h.CreateMain();
        await vm.AddPathsAsync([Folder]);

        await vm.ProcessAllCommand.ExecuteAsync(null);

        Assert.Equal(["a.mkv", "b.mp4", "c.mkv", "d.mp4"], h.Processing.Processed);
        Assert.Equal([StatusKind.Error, StatusKind.Success, StatusKind.Error, StatusKind.Success], Assert.Single(h.Dialogs.Summaries).Select(r => r.Kind));
        Assert.Single(h.Dialogs.Errors); // solo el aviso al analizar, ningún «No se puede continuar»
        Assert.DoesNotContain(vm.LogLines, l => l.Text.Contains("Se detuvo la cola"));
    }

    [Fact]
    public async Task ProcessAll_IsSequential_UpdatesRows_AndShowsSummary()
    {
        var h = new Harness();
        var videos = h.AddFolder(Folder, "a.mkv", "b.mkv", "c.mkv");
        h.Library.Statuses[videos[1]] = () => TestVideos.ImageOnly(videos[1]);
        h.Processing.Handler = async (st, progress, ct) =>
        {
            await Task.Delay(10, ct);
            return st.Selection?.Track is null
                ? new VideoOutcome { Status = VideoOutcomeStatus.Failed, Detail = "inglés solo en imagen (necesita OCR)" }
                : new VideoOutcome { Status = VideoOutcomeStatus.Completed, Detail = "10 bloques", Usage = new TokenUsage(100, 50, 0, 0), CostUsd = 0.5m };
        };
        var vm = h.CreateMain();
        await vm.AddPathsAsync([Folder]);

        await vm.ProcessAllCommand.ExecuteAsync(null);

        Assert.Equal(1, h.Processing.MaxConcurrency);
        Assert.Equal(["a.mkv", "b.mkv", "c.mkv"], h.Processing.Processed);
        Assert.False(vm.IsProcessing);
        Assert.Equal(RowState.Completed, vm.Videos[0].State);
        Assert.Equal("existe", vm.Videos[0].SpanishText);
        Assert.Equal(100, vm.Videos[0].ProgressValue);
        Assert.Equal(RowState.Failed, vm.Videos[1].State);
        Assert.Equal("inglés solo en imagen (necesita OCR)", vm.Videos[1].Detail);

        var summary = Assert.Single(h.Dialogs.Summaries);
        Assert.Equal([StatusKind.Success, StatusKind.Error, StatusKind.Success], summary.Select(r => r.Kind));
        Assert.Equal(100, vm.GlobalProgress);
        Assert.Contains("incluido en la suscripción (equivalente API ~$1.0000)", vm.UsageText);
        Assert.Contains("ent / 100 sal", vm.UsageText);
    }

    [Fact]
    public async Task ProcessAll_UsageLimit_StopsQueue_AndShowsClearWarning()
    {
        var h = new Harness();
        h.AddFolder(Folder, "a.mkv", "b.mkv", "c.mkv");
        h.Processing.Handler = (st, _, _) => Task.FromResult(Path.GetFileName(st.VideoPath) == "b.mkv"
            ? new VideoOutcome { Status = VideoOutcomeStatus.Failed, Detail = "límite de uso alcanzado (reanudable)", IsUsageLimit = true }
            : new VideoOutcome { Status = VideoOutcomeStatus.Completed, Detail = "ok" });
        var vm = h.CreateMain();
        await vm.AddPathsAsync([Folder]);

        await vm.ProcessAllCommand.ExecuteAsync(null);

        Assert.Equal(["a.mkv", "b.mkv"], h.Processing.Processed);
        Assert.Equal(MainViewModel.UsageLimitMessage, Assert.Single(h.Dialogs.Warnings));
        Assert.Equal("Se alcanzó el límite de tu plan. El avance quedó guardado; vuelve a pulsar Procesar más tarde.", MainViewModel.UsageLimitMessage);
        Assert.Equal("en pausa (límite de uso)", vm.Videos[1].ProgressText);
        Assert.Equal(RowState.Ready, vm.Videos[2].State);
        Assert.False(vm.Videos[2].IsQueued);
        Assert.Equal(2, Assert.Single(h.Dialogs.Summaries).Count);
    }

    [Fact]
    public async Task Cancel_StopsCleanly_AndLeavesRowResumable()
    {
        var h = new Harness();
        h.AddFolder(Folder, "a.mkv", "b.mkv");
        var started = new TaskCompletionSource();
        h.Processing.Handler = async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("inalcanzable");
        };
        var vm = h.CreateMain();
        await vm.AddPathsAsync([Folder]);

        var run = vm.ProcessAllCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsProcessing);
        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsProcessing);
        Assert.Equal(["a.mkv"], h.Processing.Processed);
        Assert.Equal(RowState.Cancelled, vm.Videos[0].State);
        Assert.Equal("cancelado (reanudable)", vm.Videos[0].ProgressText);
        Assert.Equal(RowState.Ready, vm.Videos[1].State);
        Assert.StartsWith("Cancelado", vm.CurrentVideoText);
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnexpectedException_InOneVideo_DoesNotStopTheQueue()
    {
        var h = new Harness();
        h.AddFolder(Folder, "a.mkv", "b.mkv");
        h.Processing.Handler = (st, _, _) => Path.GetFileName(st.VideoPath) == "a.mkv"
            ? throw new InvalidOperationException("algo raro")
            : Task.FromResult(new VideoOutcome { Status = VideoOutcomeStatus.Completed, Detail = "ok" });
        var vm = h.CreateMain();
        await vm.AddPathsAsync([Folder]);

        await vm.ProcessAllCommand.ExecuteAsync(null);

        Assert.Equal(RowState.Failed, vm.Videos[0].State);
        Assert.Contains("algo raro", vm.Videos[0].Detail);
        Assert.Contains(vm.Videos[0].LogLines, l => l.Contains("algo raro"));
        Assert.Contains(vm.LogLines, l => l.Text.Contains("algo raro"));
        Assert.Equal(RowState.Completed, vm.Videos[1].State);
    }

    [Fact]
    public async Task ConfigurationError_StopsQueue_AndShowsError()
    {
        var h = new Harness();
        h.AddFolder(Folder, "a.mkv", "b.mkv");
        h.Processing.Handler = (_, _, _) => Task.FromResult(new VideoOutcome
        {
            Status = VideoOutcomeStatus.Failed, Detail = TranslationClientFactory.MissingApiKeyMessage, IsConfiguration = true,
        });
        var vm = h.CreateMain();
        await vm.AddPathsAsync([Folder]);

        await vm.ProcessAllCommand.ExecuteAsync(null);

        Assert.Single(h.Processing.Processed);
        Assert.Contains(TranslationClientFactory.MissingApiKeyMessage, Assert.Single(h.Dialogs.Errors));
        Assert.Empty(h.Dialogs.Warnings);
    }

    [Fact]
    public async Task ProcessOne_ProcessesOnlyThatRow_WithoutSummary()
    {
        var h = new Harness();
        h.AddFolder(Folder, "a.mkv", "b.mkv");
        var vm = h.CreateMain();
        await vm.AddPathsAsync([Folder]);

        await vm.ProcessOneCommand.ExecuteAsync(vm.Videos[1]);

        Assert.Equal(["b.mkv"], h.Processing.Processed);
        Assert.Equal(RowState.Completed, vm.Videos[1].State);
        Assert.Equal(RowState.Ready, vm.Videos[0].State);
        Assert.Empty(h.Dialogs.Summaries);
    }

    [Fact]
    public async Task Retry_ReprocessesFailedRow()
    {
        var h = new Harness();
        h.AddFolder(Folder, "a.mkv");
        var fail = true;
        h.Processing.Handler = (_, _, _) => Task.FromResult(fail
            ? new VideoOutcome { Status = VideoOutcomeStatus.Failed, Detail = "error de red" }
            : new VideoOutcome { Status = VideoOutcomeStatus.Completed, Detail = "ok" });
        var vm = h.CreateMain();
        await vm.AddPathsAsync([Folder]);
        await vm.ProcessAllCommand.ExecuteAsync(null);
        Assert.True(vm.Videos[0].HasFailed);

        fail = false;
        await vm.RetryCommand.ExecuteAsync(vm.Videos[0]);

        Assert.Equal(RowState.Completed, vm.Videos[0].State);
        Assert.Equal(2, h.Processing.Processed.Count);
    }

    [Fact]
    public void QuickSelectors_PersistSettings_AndArePassedToProcessing()
    {
        var h = new Harness();
        var vm = h.CreateMain();
        Assert.Equal(TranslationBackends.ClaudeCode, vm.SelectedBackend);
        Assert.Equal("claude-opus-5-5", vm.SelectedModel);

        vm.SelectedBackend = TranslationBackends.Api;
        vm.SelectedModel = "claude-haiku-5-5";

        Assert.Equal(TranslationBackends.Api, h.Settings.Stored.Backend);
        Assert.Equal("claude-haiku-5-5", h.Settings.Stored.Model);
        Assert.Contains("costo estimado $", vm.UsageText);
    }

    [Fact]
    public async Task Processing_UsesCurrentSettings()
    {
        var h = new Harness();
        h.Settings.Stored = new AppSettings { Model = "claude-sonnet-5-5", ChunkSize = 80, CreatePlainCopy = false };
        h.AddFolder(Folder, "a.mkv");
        var vm = h.CreateMain();
        await vm.AddPathsAsync([Folder]);

        await vm.ProcessAllCommand.ExecuteAsync(null);

        var s = Assert.Single(h.Processing.SettingsSeen);
        var o = s.ToProcessingOptions();
        Assert.Equal("claude-sonnet-5-5", o.Translation.Model);
        Assert.Equal(80, o.Translation.ChunkSize);
        Assert.True(o.Translation.NoPlainCopy);
    }

    [Fact]
    public async Task RowActions_OpenFolder_ViewLog_AndGlossary()
    {
        var h = new Harness();
        var videos = h.AddFolder(Folder, "a.mkv");
        var vm = h.CreateMain();
        await vm.AddPathsAsync([Folder]);
        var row = vm.Videos[0];

        vm.OpenFolderCommand.Execute(row);
        Assert.Equal(videos[0], Assert.Single(h.Dialogs.OpenedFolders));

        vm.ViewLogCommand.Execute(row);
        Assert.Contains("a.mkv", Assert.Single(h.Dialogs.Texts).Title);

        vm.OpenGlossaryCommand.Execute(row);
        Assert.NotNull(h.Dialogs.GlossaryShown);
        Assert.Equal(@"X:\Pelis\a.glossary.json", h.Dialogs.GlossaryShown!.GlossaryPath);
    }

    [Fact]
    public async Task OpenSettings_AppliesAndPersistsResult()
    {
        var h = new Harness();
        h.Dialogs.OnSettings = svm =>
        {
            svm.Backend = TranslationBackends.Api;
            svm.Effort = "high";
            svm.ChunkSizeText = "120";
            return svm.TrySave();
        };
        var vm = h.CreateMain();

        await vm.OpenSettingsCommand.ExecuteAsync(null);

        Assert.Equal(TranslationBackends.Api, vm.SelectedBackend);
        Assert.Equal("high", h.Settings.Stored.Effort);
        Assert.Equal(120, h.Settings.Stored.ChunkSize);
    }

    [Theory]
    [InlineData("extrayendo", 0, 0, 0, 0, "", "extrayendo inglés…")]
    [InlineData("traduciendo", 0, 0, 0, 700, "glosario", "generando glosario…")]
    [InlineData("traduciendo", 2, 5, 300, 700, "tramo", "tramo 2/5 · bloques 300/700")]
    public void FormatProgress(string stage, int chunk, int chunks, int done, int total, string tStage, string expected)
    {
        var p = new VideoProgress(stage, tStage.Length == 0 ? null : new TranslationProgress(tStage, chunk, chunks, done, total, TokenUsage.Zero, 0));
        Assert.Equal(expected, VideoItemViewModel.FormatProgress(p));
    }

    [Fact]
    public void ApplyProgress_UpdatesBarAndSpanishState()
    {
        var row = new VideoItemViewModel(@"X:\a.mkv");
        row.ApplyStatus(TestVideos.Translatable(@"X:\a.mkv"));
        row.MarkQueued();
        row.BeginProcessing();
        row.ApplyProgress(new VideoProgress(VideoProgress.Translating, new TranslationProgress("tramo", 1, 4, 150, 600, TokenUsage.Zero, 0)));

        Assert.Equal(25, row.ProgressValue);
        Assert.False(row.IsProgressIndeterminate);
        Assert.Equal("en curso", row.SpanishText);
    }

    // ------------------------------------------------------------------ fase 4: herramientas y registro

    [Fact]
    public void ReportToolStatus_WarnsAboutMissingTools_AndMirrorsToFileLog()
    {
        var h = new Harness();
        h.Library.MkvMissing = true;
        h.Library.FfmpegDetected = null;
        var vm = h.CreateMain();

        vm.ReportToolStatus();

        Assert.Contains(vm.LogLines, l => l.Text.Contains("No se encontró MKVToolNix"));
        Assert.Contains(vm.LogLines, l => l.Text.Contains("No se encontró FFmpeg") && l.Text.Contains("winget install Gyan.FFmpeg"));
        Assert.Equal(2, h.ActivityLog.Lines.Count);
    }

    [Fact]
    public async Task Mp4WithoutFfmpeg_ShowsRowError_AndRescansAfterSettingFfmpegFolder()
    {
        var h = new Harness();
        var videos = h.AddFolder(Folder, "a.mp4");
        var missing = true;
        h.Library.Statuses[videos[0]] = () => missing
            ? new Core.Library.VideoStatus { VideoPath = videos[0], Error = "No se encontró FFmpeg (prueba)." }
            : TestVideos.Translatable(videos[0]);
        var vm = h.CreateMain();

        await vm.AddPathsAsync([Folder]);
        var row = Assert.Single(vm.Videos);
        Assert.Equal("error al inspeccionar", row.TrackText);
        Assert.Contains(h.ActivityLog.Lines, l => l.Contains("No se encontró FFmpeg (prueba)."));

        missing = false;
        h.Dialogs.OnSettings = s => { s.FfmpegDirectory = Path.GetTempPath(); return s.TrySave(); };
        await vm.OpenSettingsCommand.ExecuteAsync(null);

        Assert.Equal(Path.GetTempPath(), h.Library.LastTools?.Ffmpeg);
        Assert.Equal("3 (normal)", row.TrackText);
    }

    [Fact]
    public void OpenLogsFolder_OpensActivityLogFolder()
    {
        var h = new Harness();
        var vm = h.CreateMain();
        vm.OpenLogsFolderCommand.Execute(null);
        Assert.Equal(@"C:\logs", Assert.Single(h.Dialogs.OpenedFolders));
    }

    // ------------------------------------------------------------------ actualizaciones

    [Fact]
    public async Task Startup_FindsNewerVersion_ShowsBanner_AndInstalls()
    {
        var h = new Harness();
        h.Updates.Newer = FakeUpdates.Release("1.2.0");
        var vm = h.CreateMain();

        await vm.CheckForUpdatesOnStartupAsync();

        Assert.True(vm.HasUpdate);
        Assert.Contains("1.2.0", vm.UpdateBannerText);
        Assert.Equal("Actualizar ahora", vm.UpdateButtonText);

        await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Equal(@"C:\tmp\Subtitulos-Setup.exe", h.Updates.Installed);
        Assert.False(vm.IsUpdating);
    }

    [Fact]
    public async Task Startup_RespectsSetting_AndIsSilentWhenOffline()
    {
        var h = new Harness();
        h.Settings.Stored = new AppSettings { CheckForUpdates = false };
        await h.CreateMain().CheckForUpdatesOnStartupAsync();
        Assert.Equal(0, h.Updates.Checks);

        h.Settings.Stored = new AppSettings();
        h.Updates.CheckError = new UpdateException("sin conexión");
        var vm = h.CreateMain();
        await vm.CheckForUpdatesOnStartupAsync();
        Assert.False(vm.HasUpdate);
        Assert.Empty(h.Dialogs.Warnings);
        Assert.Contains(vm.LogLines, l => l.Text.Contains("sin conexión"));

        await vm.CheckUpdatesCommand.ExecuteAsync(null); // a mano sí avisa
        Assert.Single(h.Dialogs.Warnings);
    }

    [Fact]
    public async Task ManualCheck_UpToDate_SaysSo()
    {
        var h = new Harness();
        var vm = h.CreateMain();
        await vm.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.Contains("1.0.0", Assert.Single(h.Dialogs.Infos));
        Assert.False(vm.HasUpdate);
    }

    [Fact]
    public async Task Install_DevBuildOpensPage_DeclinedConfirmDoesNothing_DownloadErrorIsShown()
    {
        var h = new Harness();
        h.Updates.Newer = FakeUpdates.Release("2.0.0");
        h.Updates.CanSelfUpdate = false;
        var vm = h.CreateMain();
        await vm.CheckForUpdatesAsync(manual: false);
        Assert.Equal("Descargar", vm.UpdateButtonText);
        await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Equal("https://github.com/tostee/subtitle-extractor/releases/tag/v2.0.0", Assert.Single(h.Dialogs.OpenedUrls));
        Assert.Null(h.Updates.Installed);

        h.Updates.CanSelfUpdate = true;
        h.Dialogs.ConfirmResult = false;
        await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Null(h.Updates.Installed);

        h.Dialogs.ConfirmResult = true;
        h.Updates.DownloadError = new UpdateException("La huella SHA-256 no coincide.");
        await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Null(h.Updates.Installed);
        Assert.Contains("SHA-256", Assert.Single(h.Dialogs.Errors));
        Assert.True(vm.HasUpdate);

        vm.DismissUpdateCommand.Execute(null);
        Assert.False(vm.HasUpdate);
    }
}
