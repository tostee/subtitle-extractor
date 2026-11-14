using SubtitleTool.App.ViewModels;
using SubtitleTool.Core.Dubbing;

namespace SubtitleTool.App.Tests;

public sealed class DubbingViewModelTests
{
    private const string Video = @"X:\Pelis\Odisea.mkv";
    private const string Cuban = @"X:\Pelis\Odisea.es-cu.srt";

    /// <summary>
    /// Sin contexto de sincronización (como el hilo único de la UI: el progreso se procesa en orden). Con el contexto de
    /// xUnit, un aviso de progreso podría ejecutarse en otro hilo a la vez que el final de la operación.
    /// </summary>
    private static Task Run(Func<Task> action) => Task.Run(action);

    private static (Harness H, DubbingViewModel Vm) Create(bool withSubtitles = true, int lines = 3)
    {
        var h = new Harness();
        if (withSubtitles)
        {
            h.Dubbing.Subtitles[Video] = [(Cuban, "Cubano — Odisea.es-cu.srt")];
            h.Dubbing.Projects[Cuban] = new DubbingProject
            {
                SubtitlePath = Cuban,
                Lines = Enumerable.Range(1, lines).Select(i => new DubbingLine { Id = i, Start = 60 + i * 3, End = 62 + i * 3, Text = $"Línea {i}" }).ToList(),
            };
        }
        var vm = new DubbingViewModel(h.Dubbing, h.Dialogs, h.Settings, h.ActivityLog, Video);
        return (h, vm);
    }

    [Fact]
    public void OpensVideo_LoadsCubanSubtitles_AndSuggestsAFragment()
    {
        var (_, vm) = Create();

        Assert.Equal(Video, vm.VideoPath);
        Assert.Equal(Cuban, vm.SelectedSubtitle!.Path);
        Assert.Equal(3, vm.Lines.Count);
        Assert.Empty(vm.Characters);
        Assert.Contains("sin personajes", vm.SummaryText);
        Assert.Equal("00:01:03", vm.FragmentStart);
        Assert.Equal("00:03:03", vm.FragmentEnd);
        Assert.True(vm.GenerateCommand.CanExecute(null));
    }

    [Fact]
    public void VideoWithoutSpanishSubtitles_ExplainsWhatToDo()
    {
        var (_, vm) = Create(withSubtitles: false);

        Assert.Empty(vm.Lines);
        Assert.Contains("Versión cubana", vm.SummaryText);
        Assert.False(vm.GenerateCommand.CanExecute(null));
        Assert.False(vm.AnnotateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Annotate_FillsCharacters_WithCurrentSettings()
    {
        var (h, vm) = Create();
        h.Dubbing.Annotations[1] = ("Odiseo", "m", "ira");
        h.Dubbing.Annotations[2] = ("Penélope", "f", "sorpresa");
        h.Dubbing.Annotations[3] = ("Odiseo", "m", "calma");
        h.Settings.Stored.Model = "claude-haiku-5-5"; // cambiado en la ventana principal después de abrir esta

        await Run(() => vm.AnnotateCommand.ExecuteAsync(null));

        Assert.Equal(["Odiseo", "Penélope"], vm.Characters.Select(c => c.Name));
        Assert.Equal(2, vm.Characters[0].LineCount);
        Assert.Equal(CubanVoices.Female, vm.Characters[1].EdgeVoice);
        Assert.Equal("ira", vm.Lines[0].Emotion);
        Assert.Equal(["Odiseo", "Penélope"], vm.CharacterNames);
        Assert.Equal("claude-haiku-5-5", h.Dubbing.SettingsSeen.Single().Model);
        Assert.Contains("2 personajes", vm.SummaryText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void EditingASpeaker_CreatesTheCharacter_AndSavesOnClose()
    {
        var (h, vm) = Create();

        vm.Lines[1].Speaker = "  Telémaco ";

        Assert.Equal("Telémaco", vm.Lines[1].Speaker);
        Assert.Equal(["Telémaco"], vm.Characters.Select(c => c.Name));
        Assert.Equal(1, vm.Characters[0].LineCount);
        Assert.True(vm.CanClose());
        Assert.NotEmpty(h.Dubbing.Saved);
    }

    [Fact]
    public async Task Generate_WithoutBaseEngine_AsksToInstall()
    {
        var (h, vm) = Create();

        await Run(() => vm.GenerateCommand.ExecuteAsync(null));

        Assert.Empty(h.Dubbing.Requests);
        Assert.Contains("Motores", h.Dialogs.Warnings.Single());
    }

    [Fact]
    public async Task Generate_Fragment_SendsOptions_AndShowsResult()
    {
        var (h, vm) = Create();
        h.Dubbing.Installed.Add(DubbingEngine.Base);
        vm.FragmentStart = "1:00";
        vm.FragmentEnd = "00:02:30";
        vm.UseOwnVoices = false;

        await Run(() => vm.GenerateCommand.ExecuteAsync(null));

        var r = h.Dubbing.Requests.Single();
        Assert.Equal((TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(150)), r.Range);
        Assert.Equal(DubbingVoiceEngine.Edge, r.VoiceEngine);
        Assert.False(r.UseOwnVoices);
        Assert.EndsWith(".doblaje-cu.prueba-000100-000230.mkv", vm.LastOutputPath);
        Assert.True(vm.HasOutput);
        Assert.Contains("Terminado", vm.ProgressText);
        Assert.NotEmpty(h.Dubbing.Saved); // el proyecto se guarda antes de generar
    }

    [Theory]
    [InlineData("00:05:00", "00:04:00")]
    [InlineData("abc", "00:04:00")]
    public async Task Generate_InvalidFragment_Warns(string start, string end)
    {
        var (h, vm) = Create();
        h.Dubbing.Installed.Add(DubbingEngine.Base);
        vm.FragmentStart = start;
        vm.FragmentEnd = end;

        await Run(() => vm.GenerateCommand.ExecuteAsync(null));

        Assert.Empty(h.Dubbing.Requests);
        Assert.Contains("fragmento", h.Dialogs.Warnings.Single());
    }

    [Fact]
    public async Task Generate_Emotions_RequiresIndexTts()
    {
        var (h, vm) = Create();
        h.Dubbing.Installed.Add(DubbingEngine.Base);
        vm.UseEmotions = true;

        await Run(() => vm.GenerateCommand.ExecuteAsync(null));

        Assert.Empty(h.Dubbing.Requests);
        Assert.Contains("IndexTTS", h.Dialogs.Warnings.Single());
    }

    [Fact]
    public async Task Generate_WholeMovie_AsksFirst()
    {
        var (h, vm) = Create();
        h.Dubbing.Installed.Add(DubbingEngine.Base);
        vm.FragmentOnly = false;
        h.Dialogs.ConfirmResult = false;

        await Run(() => vm.GenerateCommand.ExecuteAsync(null));

        Assert.Empty(h.Dubbing.Requests);
    }

    [Fact]
    public async Task Generate_Error_IsShownAndLogged()
    {
        var (h, vm) = Create();
        h.Dubbing.Installed.Add(DubbingEngine.Base);
        h.Dubbing.RunError = new DubbingException("El doblaje falló: ffmpeg falló");

        await Run(() => vm.GenerateCommand.ExecuteAsync(null));

        Assert.Contains("ffmpeg", h.Dialogs.Errors.Single());
        Assert.Contains(vm.LogLines, l => l.Contains("Error: El doblaje falló"));
        Assert.False(vm.IsBusy);
        Assert.Null(vm.LastOutputPath);
    }

    [Fact]
    public async Task Cancel_StopsTheRun()
    {
        var (h, vm) = Create();
        h.Dubbing.Installed.Add(DubbingEngine.Base);
        h.Dubbing.RunGate = new TaskCompletionSource();

        var run = vm.GenerateCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.AnnotateCommand.CanExecute(null)); // una operación a la vez
        vm.CancelCommand.Execute(null);
        await run;

        Assert.False(vm.IsBusy);
        Assert.Equal("Cancelado.", vm.ProgressText);
    }

    [Fact]
    public async Task InstallEngine_ConfirmsAndRefreshesState()
    {
        var (h, vm) = Create();
        var row = vm.Engines.Single(e => e.Info.Engine == DubbingEngine.Base);
        Assert.Equal("Instalar…", row.ButtonText);

        await row.InstallCommand.ExecuteAsync(null);

        Assert.Equal([DubbingEngine.Base], h.Dubbing.InstallCalls);
        Assert.True(row.IsInstalled);
        Assert.Equal("Reinstalar…", row.ButtonText);
    }

    [Fact]
    public void ChangeEnginesFolder_IsSaved_AndMainWindowDoesNotOverwriteIt()
    {
        var h = new Harness();
        var main = h.CreateMain();
        var vm = new DubbingViewModel(h.Dubbing, h.Dialogs, h.Settings, h.ActivityLog);
        h.Dialogs.FolderToPick = @"D:\Voces";

        vm.ChangeEnginesFolderCommand.Execute(null);
        main.SelectedModel = "claude-haiku-5-5"; // la ventana principal guarda sus ajustes

        Assert.Equal(@"D:\Voces", h.Settings.Stored.DubbingEnginesDirectory);
        Assert.Equal("claude-haiku-5-5", h.Settings.Stored.Model);
        Assert.Equal(@"D:\Voces", vm.EnginesFolder);
    }

    [Fact]
    public void ChooseSample_SetsVoice()
    {
        var (h, vm) = Create();
        vm.Lines[0].Speaker = "Odiseo";
        h.Dialogs.AudioToPick = @"X:\voces\yo.ogg";

        vm.Characters[0].ChooseSampleCommand.Execute(null);

        Assert.True(vm.Characters[0].HasSample);
        Assert.Equal("yo.ogg", vm.Characters[0].SampleText);
        vm.Characters[0].ClearSampleCommand.Execute(null);
        Assert.Equal("(sin muestra)", vm.Characters[0].SampleText);
    }

    [Theory]
    [InlineData("01:24:30", 5070)]
    [InlineData("2:05", 125)]
    [InlineData("45", 45)]
    public void TryParseTime_AcceptsShortForms(string text, int seconds)
    {
        Assert.True(DubbingViewModel.TryParseTime(text, out var t));
        Assert.Equal(seconds, (int)t.TotalSeconds);
    }

    [Fact]
    public async Task MainWindow_OpensDubbingForTheSelectedVideo()
    {
        var h = new Harness();
        var videos = h.AddFolder(@"X:\Pelis", "Odisea.mkv");
        var main = h.CreateMain();
        await main.AddPathsAsync([@"X:\Pelis"]);

        main.OpenDubbingCommand.Execute(main.Videos[0]);

        Assert.Equal(videos[0], h.Dialogs.DubbingShown!.VideoPath);
    }
}
