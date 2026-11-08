using System.Collections.ObjectModel;
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
        IssuesText = string.Join("\n", report.Issues.Select(i => (i.Severity == IssueSeverity.Error ? "❌ " : "⚠️ ") + ShortMessage(i)));
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
    public ObservableCollection<ReviewRowViewModel> Rows { get; } = [];

    public bool IsDraft => _session.Source == ReviewSource.Draft;
    public string Title => $"Revisar traducción — {Path.GetFileName(_session.Paths.EnglishPath)}";
    public string PathsText => $"{Path.GetFileName(_session.Paths.EnglishPath)} → {Path.GetFileName(_session.TargetPath)} " +
        (_session.OverwritesExisting ? "(ya existe: al guardar se reemplaza dejando una copia .bak)" : "(todavía no existe: se crea al guardar)");

    public ReviewOutcome Outcome { get; private set; } = ReviewOutcome.None;

    /// <summary>La vista cierra la ventana cuando se dispara.</summary>
    public event Action? CloseRequested;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _onlyIssues;
    [ObservableProperty] private bool _onlyEdited;
    [ObservableProperty] private ReviewRowViewModel? _selectedRow;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isDirty;

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
        Rows.Clear();
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
            Rows.Add(r);
        }
        SelectedRow = selected is not null && Rows.Contains(selected) ? selected : null;
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
                return;
            }
        }
        StatusText = "No hay más bloques con avisos.";
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SaveAsync()
    {
        if (_session.OverwritesExisting
            && !_dialogs.Confirm($"Se reemplazará {Path.GetFileName(_session.TargetPath)}, que ya existe (antes se guarda una copia .bak). ¿Continuar?", "Guardar"))
            return;
        IsBusy = true;
        try
        {
            var r = await _service.SaveAsync(_session);
            Outcome = ReviewOutcome.Saved;
            _savedTexts = Snapshot();
            IsDirty = false;
            StatusText = $"Guardado: {Path.GetFileName(r.SpanishPath)}";
            if (r.PlainCopy == PlainCopyStatus.Failed)
                _dialogs.ShowWarning($"Se guardó {Path.GetFileName(r.SpanishPath)}, pero no se pudo crear la copia sin idioma {Path.GetFileName(_session.Paths.PlainCopyPath)}.");
            CloseRequested?.Invoke();
        }
        catch (ReviewException ex)
        {
            StatusText = "❌ " + ex.Message.Split('\n')[0];
            _dialogs.ShowWarning(ex.Message, "No se puede guardar");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "❌ " + ex.Message;
            _dialogs.ShowError("No se pudo guardar: " + ex.Message);
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
            _dialogs.ShowError("No se pudo guardar el borrador: " + ex.Message);
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

    private bool CanAct() => !IsBusy;
    private bool CanActOnDraft() => !IsBusy && IsDraft;

    /// <summary>Al cerrar la ventana: si hay cambios sin guardar, se pregunta.</summary>
    public bool CanClose() =>
        !IsBusy && (!IsDirty || _dialogs.Confirm(IsDraft
            ? "Hay cambios sin guardar. ¿Cerrar y perderlos? (Usa «Guardar borrador» para seguir más tarde.)"
            : "Hay cambios sin guardar. ¿Cerrar y perderlos?", "Cambios sin guardar"));
}
