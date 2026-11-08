using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubtitleTool.App.Services;
using SubtitleTool.Core.Review;
using SubtitleTool.Core.Srt;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.App.ViewModels;

/// <summary>Cómo terminó la revisión (para actualizar la fila y el registro).</summary>
public enum ReviewOutcome
{
    None,
    Saved,
    DraftSaved,
    Discarded,
}

/// <summary>Un bloque en la ventana de revisión: inglés, español editable y sus avisos.</summary>
public sealed partial class ReviewRowViewModel : ObservableObject
{
    private readonly SrtBlock _english;

    public ReviewRowViewModel(ReviewBlock block, SrtBlock english)
    {
        Block = block;
        _english = english;
        _spanish = block.Spanish;
        Revalidate();
    }

    public ReviewBlock Block { get; }
    public int Number => Block.Index;
    public string Timing => Block.TimingText;
    public string English => Block.English;
    public bool IsReadOnly => Block.IsEmptyOriginal;

    [ObservableProperty] private string _spanish;
    [ObservableProperty] private string _issuesText = "";

    /// <summary>Avisos del bloque, cada uno con su tipo (la vista pinta el icono).</summary>
    [ObservableProperty] private IReadOnlyList<IssueItem> _issues = [];
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _hasWarning;
    [ObservableProperty] private bool _isEdited;

    /// <summary>Se avisa a la ventana para recalcular contadores y estado «sin guardar».</summary>
    public event Action<ReviewRowViewModel>? Changed;

    partial void OnSpanishChanged(string value)
    {
        Block.Spanish = value ?? "";
        Revalidate();
        Changed?.Invoke(this);
    }

    /// <summary>Valida solo este bloque (rápido aunque la película tenga miles).</summary>
    private void Revalidate()
    {
        IsEdited = Block.IsEdited;
        if (Block.IsEmptyOriginal)
        {
            IssuesText = "";
            Issues = [];
            HasError = HasWarning = false;
            return;
        }
        var original = new SrtDocument([_english]);
        var translated = new SrtDocument([new SrtBlock
        {
            Index = _english.Index, TimingLine = _english.TimingLine, Start = _english.Start, End = _english.End,
            Lines = SubtitleTranslator.NormalizeLines(Block.Spanish),
        }]);
        var report = SrtValidator.Validate(original, translated);
        HasError = report.HasErrors;
        HasWarning = report.WarningCount > 0;
        Issues = report.Issues
            .Select(i => new IssueItem(i.Severity == IssueSeverity.Error ? StatusKind.Error : StatusKind.Warning, ShortMessage(i)))
            .ToList();
        IssuesText = string.Join("\n", Issues);
    }

    private static string ShortMessage(ValidationIssue i) => i.Kind switch
    {
        IssueKind.EmptyTranslation => "vacío",
        IssueKind.TooManyLines => $"más de {SrtValidator.MaxLines} líneas",
        IssueKind.LineTooLong => $"línea de más de {SrtValidator.MaxLineLength} caracteres",
        IssueKind.TagMismatch => "etiquetas distintas al inglés",
        IssueKind.DialogueDashMismatch => "guiones de diálogo distintos",
        _ => i.Message,
    };
}

/// <summary>Ventana «Revisar traducción»: inglés y español lado a lado antes de guardar el .es.srt.</summary>
public sealed partial class ReviewViewModel : ObservableObject
{
    private static readonly CompareInfo Compare = CultureInfo.GetCultureInfo("es-ES").CompareInfo;
    private const CompareOptions SearchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    private readonly ReviewSession _session;
    private readonly IReviewService _service;
    private readonly IDialogService _dialogs;
    private Dictionary<int, string> _savedTexts;

    public ReviewViewModel(ReviewSession session, IReviewService service, IDialogService dialogs)
    {
        _session = session;
        _service = service;
        _dialogs = dialogs;
        AllRows = session.Blocks.Select(b => new ReviewRowViewModel(b, session.English.Blocks[b.Id - 1])).ToList();
        foreach (var r in AllRows)
            r.Changed += OnRowChanged;
        _savedTexts = Snapshot();
        ApplyFilter();
        UpdateSummary();
    }

    public IReadOnlyList<ReviewRowViewModel> AllRows { get; }

    /// <summary>Filas visibles (filtradas). Se sustituye la lista entera de una vez: con miles de bloques es mucho más
    /// rápido que vaciar y volver a llenar una colección observable fila a fila.</summary>
    [ObservableProperty] private IReadOnlyList<ReviewRowViewModel> _rows = [];

    public bool IsDraft => _session.Source == ReviewSource.Draft;
    public string Title => $"Revisar traducción — {Path.GetFileName(_session.Paths.EnglishPath)}";
    public string PathsText => $"{Path.GetFileName(_session.Paths.EnglishPath)} → {Path.GetFileName(_session.TargetPath)} " +
        (_session.OverwritesExisting ? "(ya existe: al guardar se reemplaza dejando una copia .bak)" : "(todavía no existe: se crea al guardar)");

    public ReviewOutcome Outcome { get; private set; } = ReviewOutcome.None;

    /// <summary>La vista cierra la ventana cuando se dispara.</summary>
    public event Action? CloseRequested;

    /// <summary>«Siguiente aviso» encontró un bloque: la vista lleva el foco a su español para corregirlo.</summary>
    public event Action<ReviewRowViewModel>? FocusRowRequested;

    public const string NoIssuesMessage = "No hay bloques con avisos en la lista actual.";
    public const string BusyCloseMessage = "Espera a que termine de guardar para cerrar.";

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _onlyIssues;
    [ObservableProperty] private bool _onlyEdited;
    [ObservableProperty] private ReviewRowViewModel? _selectedRow;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _statusText = "";

    /// <summary>Tipo del mensaje de estado (icono). Se vuelve a None si se cambia el texto sin indicar tipo.</summary>
    [ObservableProperty] private StatusKind _statusKind;

    [ObservableProperty] private bool _isDirty;

    partial void OnStatusTextChanged(string value) => StatusKind = StatusKind.None;

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText = text;
        StatusKind = kind;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveDraftCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    private bool _isBusy;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnOnlyIssuesChanged(bool value) => ApplyFilter();
    partial void OnOnlyEditedChanged(bool value) => ApplyFilter();

    private Dictionary<int, string> Snapshot() => AllRows.ToDictionary(r => r.Block.Id, r => r.Spanish);

    private void OnRowChanged(ReviewRowViewModel row)
    {
        IsDirty = AllRows.Any(r => !string.Equals(r.Spanish, _savedTexts[r.Block.Id], StringComparison.Ordinal));
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var errors = AllRows.Count(r => r.HasError);
        var warnings = AllRows.Count(r => r.HasWarning && !r.HasError);
        var edited = AllRows.Count(r => r.IsEdited);
        SummaryText = $"{AllRows.Count} bloques · {warnings} con avisos · {errors} con errores · {edited} editados · mostrando {Rows.Count}";
    }

    public void ApplyFilter()
    {
        var search = SearchText?.Trim() ?? "";
        var selected = SelectedRow;
        var rows = new List<ReviewRowViewModel>(AllRows.Count);
        foreach (var r in AllRows)
        {
            if (OnlyIssues && !r.HasWarning && !r.HasError)
                continue;
            if (OnlyEdited && !r.IsEdited)
                continue;
            if (search.Length > 0
                && Compare.IndexOf(r.English, search, SearchOptions) < 0
                && Compare.IndexOf(r.Spanish, search, SearchOptions) < 0
                && r.Number.ToString(CultureInfo.InvariantCulture) != search)
                continue;
            rows.Add(r);
        }
        Rows = rows;
        SelectedRow = selected is not null && rows.Contains(selected) ? selected : null;
        UpdateSummary();
    }

    /// <summary>Salta al siguiente bloque con aviso o error (vuelve al principio al llegar al final).</summary>
    [RelayCommand]
    private void NextIssue()
    {
        var list = Rows.ToList();
        if (list.Count == 0)
            return;
        var start = SelectedRow is null ? -1 : list.IndexOf(SelectedRow);
        for (var step = 1; step <= list.Count; step++)
        {
            var r = list[(start + step) % list.Count];
            if (r.HasError || r.HasWarning)
            {
                SelectedRow = r;
                if (StatusText == NoIssuesMessage)
                    StatusText = "";
                FocusRowRequested?.Invoke(r);
                return;
            }
        }
        SetStatus(NoIssuesMessage, StatusKind.Info);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SaveAsync()
    {
        if (_session.OverwritesExisting
            && !_dialogs.Confirm($"Se reemplazará {Path.GetFileName(_session.TargetPath)}, que ya existe (antes se guarda una copia .bak). ¿Continuar?", "Guardar"))
            return;
        IsBusy = true;
        SetStatus("Guardando…", StatusKind.InProgress);
        try
        {
            var r = await _service.SaveAsync(_session);
            Outcome = ReviewOutcome.Saved;
            _savedTexts = Snapshot();
            IsDirty = false;
            SetStatus($"Guardado: {Path.GetFileName(r.SpanishPath)}", StatusKind.Success);
            if (r.PlainCopy == PlainCopyStatus.Failed)
                _dialogs.ShowWarning($"Se guardó {Path.GetFileName(r.SpanishPath)}, pero no se pudo crear la copia sin idioma {Path.GetFileName(_session.Paths.PlainCopyPath)}.");
            CloseRequested?.Invoke();
        }
        catch (ReviewException ex)
        {
            SetStatus(ex.Message.Split('\n')[0], StatusKind.Error);
            _dialogs.ShowWarning(ex.Message, "No se puede guardar");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("No se pudo guardar el archivo.", StatusKind.Error);
            _dialogs.ShowError("No se pudo guardar el archivo. Comprueba que no esté abierto en otro programa y que tengas permiso " +
                               "para escribir en la carpeta.\n\nDetalle: " + ex.Message, "No se pudo guardar");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanActOnDraft))]
    private async Task SaveDraftAsync()
    {
        IsBusy = true;
        SetStatus("Guardando borrador…", StatusKind.InProgress);
        try
        {
            await _service.SaveDraftAsync(_session);
            Outcome = ReviewOutcome.DraftSaved;
            _savedTexts = Snapshot();
            IsDirty = false;
            CloseRequested?.Invoke();
        }
        catch (Exception ex) when (ex is ReviewException or IOException or UnauthorizedAccessException)
        {
            SetStatus("No se pudo guardar el borrador.", StatusKind.Error);
            _dialogs.ShowError("No se pudo guardar el borrador: " + ex.Message, "No se pudo guardar");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanActOnDraft))]
    private void Discard()
    {
        if (!_dialogs.Confirm("¿Descartar esta traducción? Se borra el borrador (con tus cambios) y habrá que volver a traducir el video.", "Descartar traducción"))
            return;
        try
        {
            _service.Discard(_session.Paths.EnglishPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("No se pudo descartar: " + ex.Message);
            return;
        }
        Outcome = ReviewOutcome.Discarded;
        IsDirty = false;
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Guardado pedido al cerrar. Los errores previstos ya los tratan los comandos; cualquier otro se muestra aquí
    /// (no se pierde en una tarea sin observar) y la ventana sigue abierta con los cambios.
    /// </summary>
    internal async Task SaveBeforeCloseAsync()
    {
        try
        {
            await (IsDraft ? SaveDraftCommand.ExecuteAsync(null) : SaveCommand.ExecuteAsync(null));
        }
        catch (Exception ex)
        {
            IsBusy = false;
            SetStatus("No se pudo guardar: los cambios siguen en la ventana.", StatusKind.Error);
            _dialogs.ShowError("No se pudo guardar. Los cambios siguen en la ventana; vuelve a intentarlo.\n\nDetalle: " + ex.Message,
                "No se pudo guardar");
        }
    }

    private bool CanAct() => !IsBusy;
    private bool CanActOnDraft() => !IsBusy && IsDraft;

    /// <summary>
    /// Al cerrar la ventana. Si hay cambios sin guardar se pregunta «¿Guardar?» (Sí / No / Cancelar): «Sí» lanza el
    /// guardado (borrador o .es.srt) y devuelve false; la ventana se cierra sola cuando el guardado termina bien.
    /// </summary>
    public bool CanClose()
    {
        if (IsBusy)
        {
            SetStatus(BusyCloseMessage, StatusKind.Warning);
            return false;
        }
        if (!IsDirty)
            return true;
        var answer = _dialogs.AskSaveChanges(IsDraft
            ? "Hay cambios sin guardar. ¿Guardarlos en el borrador antes de cerrar?\n\n" +
              "Sí: guardar el borrador (puedes seguir la revisión más tarde).\nNo: cerrar y perder los cambios.\nCancelar: seguir revisando."
            : $"Hay cambios sin guardar. ¿Guardarlos en {Path.GetFileName(_session.TargetPath)} antes de cerrar?\n\n" +
              "Sí: guardar.\nNo: cerrar y perder los cambios.\nCancelar: seguir revisando.",
            "Cambios sin guardar");
        switch (answer)
        {
            case true:
                // Fuera del evento Closing (se encola en el hilo de la UI) para no reentrar en el cierre.
                if (SynchronizationContext.Current is { } ui)
                    ui.Post(_ => _ = SaveBeforeCloseAsync(), null);
                else
                    _ = SaveBeforeCloseAsync();
                return false;
            case false:
                return true;
            default:
                return false;
        }
    }
}
