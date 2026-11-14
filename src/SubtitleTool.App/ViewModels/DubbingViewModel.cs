using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubtitleTool.App.Models;
using SubtitleTool.App.Services;
using SubtitleTool.Core.Dubbing;
using SubtitleTool.Core.Srt;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.App.ViewModels;

/// <summary>
/// Ventana de doblaje: elegir video y subtítulos, detectar personajes y emociones con Claude, asignar voces,
/// instalar los motores y generar el video doblado (completo o un fragmento de prueba).
/// </summary>
public sealed partial class DubbingViewModel : ObservableObject
{
    private readonly IDubbingService _dubbing;
    private readonly IDialogService _dialogs;
    private readonly ISettingsStore _settingsStore;
    private readonly IActivityLog _log;
    private AppSettings _settings;
    private DubbingProject? _project;
    private CancellationTokenSource? _cts;

    public DubbingViewModel(IDubbingService dubbing, IDialogService dialogs, ISettingsStore settingsStore, IActivityLog? log = null,
        string? videoPath = null)
    {
        _dubbing = dubbing;
        _dialogs = dialogs;
        _settingsStore = settingsStore;
        _log = log ?? NullActivityLog.Instance;
        _settings = settingsStore.Load().Normalize();
        foreach (var info in DubbingEngines.All)
            Engines.Add(new DubbingEngineRowViewModel(info, this));
        RefreshEngines();
        if (videoPath is not null)
            SetVideo(videoPath);
    }

    public static IReadOnlyList<Choice> GenderChoices { get; } = [new("m", "Hombre"), new("f", "Mujer"), new("?", "No se sabe")];
    public static IReadOnlyList<Choice> VoiceChoices { get; } = CubanVoices.All.Select(v => new Choice(v.Id, v.Label)).ToList();
    public static IReadOnlyList<Choice> EmotionChoices { get; } = DubbingEmotions.All.Select(e => new Choice(e, DubbingEmotions.Label(e))).ToList();

    public ObservableCollection<SubtitleOption> SubtitleOptions { get; } = [];
    public ObservableCollection<DubbingCharacterViewModel> Characters { get; } = [];
    public ObservableCollection<DubbingLineViewModel> Lines { get; } = [];
    public ObservableCollection<DubbingEngineRowViewModel> Engines { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    /// <summary>Nombres de personajes para el desplegable de cada línea.</summary>
    public ObservableCollection<string> CharacterNames { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(HasVideo))]
    private string? _videoPath;

    [ObservableProperty] private SubtitleOption? _selectedSubtitle;
    [ObservableProperty] private string _summaryText = "Elige un video que ya tenga subtítulos en español (mejor la versión cubana).";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnnotateCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseVideoCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChangeEnginesFolderCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    private string? _lastOutputPath;
    [ObservableProperty] private string _gpuText = "";

    // Opciones de generación
    [ObservableProperty] private bool _useEmotions;
    [ObservableProperty] private bool _useOwnVoices = true;
    [ObservableProperty] private bool _fragmentOnly = true;
    [ObservableProperty] private string _fragmentStart = "00:00:00";
    [ObservableProperty] private string _fragmentEnd = "00:02:00";
    [ObservableProperty] private double _emotionStrength = 1.0;

    public bool IsIdle => !IsBusy;
    public bool HasVideo => VideoPath is not null;
    public bool HasOutput => LastOutputPath is not null;
    public bool HasProject => _project is not null;
    public string Title => VideoPath is null ? "Doblaje con voces" : $"Doblaje con voces — {Path.GetFileName(VideoPath)}";
    public string EnginesFolder => _dubbing.EnginesRoot(_settings);
    public AppSettings Settings => _settings;

    // ---------- Video y subtítulos ----------

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void ChooseVideo()
    {
        if (_dialogs.PickVideoFile() is { } path)
            SetVideo(path);
    }

    public void SetVideo(string path)
    {
        SaveProject();
        VideoPath = Path.GetFullPath(path);
        LastOutputPath = null;
        SelectedSubtitle = null; // si se vuelve a un video ya visto, su subtítulo se recarga
        SubtitleOptions.Clear();
        foreach (var s in _dubbing.FindSubtitles(VideoPath))
            SubtitleOptions.Add(new SubtitleOption(s.Path, s.Label));
        if (SubtitleOptions.Count == 0)
        {
            ClearProject();
            SummaryText = "Este video no tiene subtítulos en español junto a él (.es-cu.srt o .es.srt). " +
                          "Tradúcelo primero en la ventana principal (menú contextual → Versión cubana).";
            return;
        }
        SelectedSubtitle = SubtitleOptions[0];
        LastOutputPath = null;
    }

    partial void OnSelectedSubtitleChanged(SubtitleOption? value)
    {
        if (value is not { } s)
            return;
        SaveProject();
        try
        {
            LoadProject(_dubbing.LoadProject(s.Path));
        }
        catch (Exception ex) when (ex is SrtParseException or IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException)
        {
            ClearProject();
            SummaryText = "No se pudo leer el subtítulo: " + ex.Message;
        }
        if (_project is { } p && p.Lines.Count > 0)
        {
            // Fragmento de prueba por defecto: los primeros ~2 minutos con diálogo.
            var first = TimeSpan.FromSeconds(Math.Floor(p.Lines[0].Start));
            FragmentStart = FormatTime(first);
            FragmentEnd = FormatTime(first + TimeSpan.FromMinutes(2));
        }
    }

    private void ClearProject()
    {
        _project = null;
        Lines.Clear();
        Characters.Clear();
        CharacterNames.Clear();
        OnPropertyChanged(nameof(HasProject));
        AnnotateCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
    }

    private void LoadProject(DubbingProject project)
    {
        _project = project;
        Lines.Clear();
        foreach (var l in project.Lines)
            Lines.Add(new DubbingLineViewModel(l, this));
        RebuildCharacters();
        OnPropertyChanged(nameof(HasProject));
        AnnotateCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
        UpdateSummary();
    }

    /// <summary>Tras cambiar el personaje de una línea: la lista de personajes se ajusta (se añaden o quitan).</summary>
    internal void OnLineSpeakerChanged()
    {
        if (_project is null)
            return;
        var before = Characters.Select(c => c.Name).ToList();
        _project.SyncCharacters();
        if (!before.SequenceEqual(_project.Characters.Select(c => c.Name)))
            RebuildCharacters();
        else
            foreach (var c in Characters)
                c.RefreshLineCount();
        UpdateSummary();
    }

    private void RebuildCharacters()
    {
        Characters.Clear();
        CharacterNames.Clear();
        if (_project is null)
            return;
        foreach (var c in _project.Characters)
        {
            Characters.Add(new DubbingCharacterViewModel(c, this));
            CharacterNames.Add(c.Name);
        }
    }

    internal int LineCountOf(string name) =>
        _project?.Lines.Count(l => string.Equals(l.Speaker, name, StringComparison.OrdinalIgnoreCase)) ?? 0;

    private void UpdateSummary()
    {
        if (_project is not { } p)
            return;
        var withSpeaker = p.Lines.Count(l => l.Speaker.Length > 0);
        var emotional = p.Lines.Count(l => l.Emotion != DubbingEmotions.Neutral);
        SummaryText = p.Annotated
            ? $"{p.Lines.Count} líneas · {Characters.Count} personajes · {withSpeaker} líneas con personaje · {emotional} con emoción."
            : $"{p.Lines.Count} líneas sin personajes todavía. Pulsa «Detectar personajes y emociones» (usa Claude con el modo de los ajustes) " +
              "o asígnalos a mano en la pestaña Líneas.";
    }

    public void SaveProject()
    {
        if (_project is null)
            return;
        try
        {
            _dubbing.SaveProject(_project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog("No se pudo guardar el proyecto de doblaje: " + ex.Message);
        }
    }

    // ---------- Personajes y emociones (Claude) ----------

    private bool CanAnnotate() => IsIdle && _project is not null;

    [RelayCommand(CanExecute = nameof(CanAnnotate))]
    private async Task AnnotateAsync()
    {
        if (_project is not { } project)
            return;
        if (project.Annotated && !_dialogs.Confirm(
                "Las líneas ya tienen personajes y emociones. ¿Volver a detectarlos con Claude? Se sustituyen los de todas las líneas " +
                "(las voces y muestras de los personajes que sigan existiendo se conservan).", "Doblaje"))
            return;
        await RunBusyAsync("Detectando personajes y emociones con Claude…", async ct =>
        {
            var finished = false;
            var progress = new UiProgress<AnnotationProgress>(p =>
            {
                if (finished) return; // aviso atrasado: no pisa el estado final
                ProgressValue = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
                ProgressText = $"Personajes y emociones: {p.Done} de {p.Total} líneas";
            });
            TokenUsage usage;
            try
            {
                usage = await _dubbing.AnnotateAsync(project, _settings, progress, ct);
            }
            finally
            {
                finished = true;
            }
            LoadProject(project);
            AddLog($"Personajes y emociones detectados: {Characters.Count} personajes ({usage.InputTokens + usage.OutputTokens:N0} tokens).");
        });
    }

    // ---------- Motores ----------

    public void RefreshEngines()
    {
        foreach (var e in Engines)
            e.Refresh(_dubbing.IsInstalled(e.Info.Engine, _settings));
        OnPropertyChanged(nameof(EnginesFolder));
    }

    public async Task DetectGpuAsync()
    {
        var gpu = await _dubbing.DetectGpuAsync();
        GpuText = gpu is null
            ? "No se detectó una GPU NVIDIA: las voces propias y las emociones funcionarían muy despacio (solo con el procesador)."
            : $"GPU: {gpu}";
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void ChangeEnginesFolder()
    {
        var folder = _dialogs.PickFolder("Carpeta para los motores de voz (necesita varios GB libres)");
        if (folder is null)
            return;
        _settings = _settingsStore.Load().Normalize();
        _settings.DubbingEnginesDirectory = folder;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("No se pudo guardar la carpeta de motores: " + ex.Message, "Doblaje");
            return;
        }
        RefreshEngines();
        AddLog("Carpeta de motores: " + EnginesFolder);
    }

    internal async Task InstallAsync(DubbingEngineRowViewModel row)
    {
        if (IsBusy)
            return;
        var info = row.Info;
        if (row.IsInstalled && !_dialogs.Confirm($"«{info.Name}» ya está instalado. ¿Reinstalarlo?", "Doblaje"))
            return;
        if (!_dialogs.Confirm($"Se descargará «{info.Name}» ({info.Size}) en:\n{EnginesFolder}\n\nPuede tardar bastante. ¿Continuar?", "Instalar motor"))
            return;
        await RunBusyAsync($"Instalando {info.Name}…", async ct =>
        {
            IsProgressIndeterminate = true;
            var finished = false;
            var log = new UiProgress<string>(line =>
            {
                if (finished) return; // aviso atrasado: no pisa el estado final
                ProgressText = line.Trim();
                AddLog(line);
            });
            try
            {
                await _dubbing.InstallAsync(info.Engine, _settings, log, ct);
            }
            finally
            {
                finished = true;
            }
            AddLog($"{info.Name}: instalado.");
        });
        RefreshEngines();
    }

    // ---------- Generar ----------

    private bool CanGenerate() => IsIdle && _project is not null && VideoPath is not null;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (_project is not { } project || VideoPath is not { } video)
            return;
        (TimeSpan, TimeSpan)? range = null;
        if (FragmentOnly)
        {
            if (!TryParseTime(FragmentStart, out var a) || !TryParseTime(FragmentEnd, out var b) || b <= a)
            {
                _dialogs.ShowWarning("El fragmento no es válido: escribe el inicio y el fin como hh:mm:ss (el fin después del inicio).", "Doblaje");
                return;
            }
            range = (a, b);
        }
        var request = new DubbingRequest
        {
            VideoPath = video,
            Project = project.Clone(), // el motor lee una copia: la ventana puede seguir viva sin tocarla a la vez
            VoiceEngine = UseEmotions ? DubbingVoiceEngine.Emotions : DubbingVoiceEngine.Edge,
            UseOwnVoices = UseOwnVoices,
            Range = range,
            EmotionStrength = EmotionStrength,
        };
        var lostSamples = UseOwnVoices ? Characters.Where(c => c.HasSample && !File.Exists(c.SamplePath)).Select(c => c.Name).ToList() : [];
        if (lostSamples.Count > 0)
            AddLog("No se encuentra la muestra de voz de: " + string.Join(", ", lostSamples) + ". Se usará su voz base.");
        var missing = MissingEngines();
        if (missing.Count > 0)
        {
            _dialogs.ShowWarning("Antes hay que instalar: " + string.Join(", ", missing) + ".\n\nVe a la pestaña «Motores».", "Doblaje");
            return;
        }
        if (range is null && !_dialogs.Confirm(
                $"Se va a doblar la película entera ({project.Lines.Count} líneas)" +
                (UseEmotions ? ", con emociones (unos 10 s por línea: puede tardar horas)" : "") +
                ". Lo ya generado se reutiliza si lo cancelas y vuelves a empezar. ¿Continuar?", "Doblaje"))
            return;
        SaveProject();
        LastOutputPath = null;
        await RunBusyAsync(range is null ? "Doblando la película…" : "Doblando el fragmento…", async ct =>
        {
            var finished = false;
            var progress = new UiProgress<DubbingProgress>(p =>
            {
                if (finished) return; // aviso atrasado: no pisa el estado final
                IsProgressIndeterminate = p.Total == 0;
                ProgressValue = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
                ProgressText = p.Total > 0 && p.Phase != "mezcla"
                    ? $"{p.PhaseLabel}: {p.Done} de {p.Total}"
                    : p.PhaseLabel + (p.Phase == "mezcla" ? $" ({p.Done} %)" : "…");
                if (p.Done == 0 || p.Phase is "tiempos")
                    AddLog(p.Message);
            });
            DubbingResult result;
            try
            {
                result = await _dubbing.RunAsync(request, _settings, progress, ct);
            }
            finally
            {
                finished = true;
            }
            LastOutputPath = result.OutputPath;
            ProgressValue = 100;
            ProgressText = "Terminado: " + Path.GetFileName(result.OutputPath);
            AddLog($"Doblaje terminado: {result.OutputPath}. {result.Lines} líneas, {result.SpedUp} aceleradas, " +
                   $"{result.Overflowing} se pasan de su hueco (retraso máximo {result.MaxDelaySeconds:0.0} s).");
            _log.Write("Doblaje: " + result.OutputPath);
        });
    }

    /// <summary>Motores que faltan para las opciones elegidas.</summary>
    public IReadOnlyList<string> MissingEngines()
    {
        var missing = new List<string>();
        if (!_dubbing.IsInstalled(DubbingEngine.Base, _settings))
            missing.Add(DubbingEngines.All[0].Name);
        if (UseEmotions && !_dubbing.IsInstalled(DubbingEngine.IndexTts, _settings))
            missing.Add(DubbingEngines.All[2].Name);
        if (UseOwnVoices && Characters.Any(c => c.HasSample && File.Exists(c.SamplePath)) && !_dubbing.IsInstalled(DubbingEngine.SeedVc, _settings))
            missing.Add(DubbingEngines.All[1].Name);
        return missing;
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        // Primero el texto: al cancelar, la operación puede terminar en esta misma llamada (y poner «Cancelado.»).
        ProgressText = "Cancelando…";
        _cts?.Cancel();
    }

    [RelayCommand]
    private void OpenOutput()
    {
        if (LastOutputPath is { } p && File.Exists(p))
            _dialogs.OpenFile(p);
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if ((LastOutputPath ?? VideoPath) is { } p)
            _dialogs.OpenFolder(p);
    }

    /// <summary>Ejecuta una operación larga: una sola a la vez, cancelable, con los errores al registro y a un aviso.</summary>
    private async Task RunBusyAsync(string start, Func<CancellationToken, Task> work)
    {
        IsBusy = true;
        // Ajustes al día (modo y modelo pueden haber cambiado en la ventana principal).
        _settings = _settingsStore.Load().Normalize();
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = start;
        AddLog(start);
        _cts = new CancellationTokenSource();
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Cancelado.";
            AddLog("Cancelado. Lo ya generado se reutiliza la próxima vez.");
        }
        catch (Exception ex) when (ex is DubbingException or DubbingInstallException or AnnotationException or TranslationClientException
                                       or IOException or UnauthorizedAccessException)
        {
            ProgressText = "Error.";
            AddLog("Error: " + ex.Message);
            _log.WriteException("Doblaje", ex);
            _dialogs.ShowError(ex.Message, "Doblaje");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsProgressIndeterminate = false;
            IsBusy = false;
        }
    }

    internal void AddLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {message.TrimEnd()}");
        while (LogLines.Count > 500)
            LogLines.RemoveAt(0);
    }

    internal string? PickSample(string characterName) =>
        _dialogs.PickAudioFile($"Muestra de voz para «{characterName}» (10–25 s, una sola persona, sin música)");

    /// <summary>Al cerrar: si hay algo en marcha, se pregunta; el proyecto se guarda.</summary>
    public bool CanClose()
    {
        if (IsBusy && !_dialogs.Confirm("Hay una operación en marcha. ¿Cancelarla y cerrar? (lo ya generado se conserva)", "Doblaje"))
            return false;
        _cts?.Cancel();
        SaveProject();
        return true;
    }

    public static string FormatTime(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    public static bool TryParseTime(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3 || parts.Any(p => !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            return false;
        var n = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).Reverse().ToArray();
        value = TimeSpan.FromSeconds(n[0] + (n.Length > 1 ? n[1] * 60 : 0) + (n.Length > 2 ? n[2] * 3600 : 0));
        return true;
    }
}

/// <summary>
/// Como <see cref="Progress{T}"/>, pero sin contexto de UI (pruebas) avisa en el momento en vez de en el pool de hilos,
/// para que un aviso atrasado no pise el estado final.
/// </summary>
public sealed class UiProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public void Report(T value)
    {
        if (_context is null)
            handler(value);
        else
            _context.Post(_ => handler(value), null);
    }
}

/// <summary>Subtítulo elegible para doblar.</summary>
public sealed record SubtitleOption(string Path, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Un personaje en la tabla: voz base, velocidad, tono y muestra de voz.</summary>
public sealed partial class DubbingCharacterViewModel : ObservableObject
{
    private readonly DubbingCharacter _model;
    private readonly DubbingViewModel _owner;

    public DubbingCharacterViewModel(DubbingCharacter model, DubbingViewModel owner)
    {
        _model = model;
        _owner = owner;
        _lineCount = owner.LineCountOf(model.Name);
    }

    public string Name => _model.Name;

    [ObservableProperty] private int _lineCount;

    public string Gender
    {
        get => _model.Gender;
        set
        {
            if (_model.Gender == value) return;
            _model.Gender = value;
            OnPropertyChanged();
        }
    }

    public string EdgeVoice
    {
        get => _model.EdgeVoice;
        set
        {
            if (_model.EdgeVoice == value) return;
            _model.EdgeVoice = value;
            OnPropertyChanged();
        }
    }

    public int Rate
    {
        get => _model.Rate;
        set
        {
            value = Math.Clamp(value, -50, 100);
            if (_model.Rate == value) return;
            _model.Rate = value;
            OnPropertyChanged();
        }
    }

    public int Pitch
    {
        get => _model.Pitch;
        set
        {
            value = Math.Clamp(value, -50, 50);
            if (_model.Pitch == value) return;
            _model.Pitch = value;
            OnPropertyChanged();
        }
    }

    public string? SamplePath => _model.SamplePath;
    public bool HasSample => !string.IsNullOrWhiteSpace(_model.SamplePath);
    public string SampleText => HasSample ? Path.GetFileName(_model.SamplePath!) : "(sin muestra)";

    public void RefreshLineCount() => LineCount = _owner.LineCountOf(Name);

    [RelayCommand]
    private void ChooseSample()
    {
        if (_owner.PickSample(Name) is not { } path)
            return;
        _model.SamplePath = path;
        NotifySample();
        _owner.SaveProject();
    }

    [RelayCommand]
    private void ClearSample()
    {
        _model.SamplePath = null;
        NotifySample();
        _owner.SaveProject();
    }

    private void NotifySample()
    {
        OnPropertyChanged(nameof(SamplePath));
        OnPropertyChanged(nameof(HasSample));
        OnPropertyChanged(nameof(SampleText));
    }
}

/// <summary>Una línea en la tabla: personaje, emoción e intensidad editables.</summary>
public sealed partial class DubbingLineViewModel : ObservableObject
{
    private readonly DubbingLine _model;
    private readonly DubbingViewModel _owner;

    public DubbingLineViewModel(DubbingLine model, DubbingViewModel owner)
    {
        _model = model;
        _owner = owner;
    }

    public int Id => _model.Id;
    public string TimeText => DubbingViewModel.FormatTime(TimeSpan.FromSeconds(_model.Start));
    public string Text => _model.Text.Replace("\n", " / ");

    public string Speaker
    {
        get => _model.Speaker;
        set
        {
            value = (value ?? "").Trim();
            if (_model.Speaker == value) return;
            _model.Speaker = value;
            OnPropertyChanged();
            _owner.OnLineSpeakerChanged();
        }
    }

    public string Emotion
    {
        get => _model.Emotion;
        set
        {
            value = DubbingEmotions.All.Contains(value) ? value : DubbingEmotions.Neutral;
            if (_model.Emotion == value) return;
            _model.Emotion = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Intensidad en % (0–100) para la tabla.</summary>
    public int IntensityPercent
    {
        get => (int)Math.Round(_model.Intensity * 100);
        set
        {
            var v = Math.Clamp(value, 0, 100) / 100.0;
            if (Math.Abs(_model.Intensity - v) < 0.001) return;
            _model.Intensity = v;
            OnPropertyChanged();
        }
    }
}

/// <summary>Un motor en la pestaña Motores: estado e instalación.</summary>
public sealed partial class DubbingEngineRowViewModel : ObservableObject
{
    private readonly DubbingViewModel _owner;

    public DubbingEngineRowViewModel(DubbingEngineInfo info, DubbingViewModel owner)
    {
        Info = info;
        _owner = owner;
    }

    public DubbingEngineInfo Info { get; }
    public string Name => Info.Name;
    public string Description => Info.Description + (Info.NeedsGpu ? " Necesita una GPU NVIDIA." : "");
    public string Size => Info.Size;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    [NotifyPropertyChangedFor(nameof(StatusKind))]
    private bool _isInstalled;

    public string StatusText => IsInstalled ? "Instalado" : "No instalado";
    public string ButtonText => IsInstalled ? "Reinstalar…" : "Instalar…";
    public StatusKind StatusKind => IsInstalled ? StatusKind.Success : StatusKind.Pending;

    public void Refresh(bool installed) => IsInstalled = installed;

    [RelayCommand]
    private Task InstallAsync() => _owner.InstallAsync(this);
}
