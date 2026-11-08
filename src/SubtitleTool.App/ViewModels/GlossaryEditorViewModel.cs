using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubtitleTool.App.Models;
using SubtitleTool.App.Services;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.App.ViewModels;

public sealed partial class TermRow : ObservableObject
{
    [ObservableProperty] private string _en = "";
    [ObservableProperty] private string _es = "";
    [ObservableProperty] private string _note = "";
}

public sealed partial class AddressRow : ObservableObject
{
    [ObservableProperty] private string _a = "";
    [ObservableProperty] private string _b = "";
    [ObservableProperty] private string _form = "tú";
    [ObservableProperty] private string _note = "";
}

/// <summary>Editor de <c>&lt;video&gt;.glossary.json</c>: términos y tratamientos (tú/usted).</summary>
public sealed partial class GlossaryEditorViewModel : ObservableObject
{
    public static IReadOnlyList<string> Forms { get; } = ["tú", "usted"];

    private readonly IGlossaryService _service;
    private readonly IDialogService _dialogs;
    private readonly Func<AppSettings> _settings;

    public GlossaryEditorViewModel(string glossaryPath, string englishPath, IGlossaryService service, IDialogService dialogs, Func<AppSettings> settings)
    {
        GlossaryPath = glossaryPath;
        EnglishPath = englishPath;
        _service = service;
        _dialogs = dialogs;
        _settings = settings;
    }

    public string GlossaryPath { get; }
    public string EnglishPath { get; }
    public string Title => "Glosario — " + Path.GetFileName(GlossaryPath);

    public ObservableCollection<TermRow> Terms { get; } = [];
    public ObservableCollection<AddressRow> Address { get; } = [];

    [ObservableProperty] private string _statusText = "";

    /// <summary>Tipo del mensaje de estado (icono). Se vuelve a None si se cambia el texto sin indicar tipo.</summary>
    [ObservableProperty] private StatusKind _statusKind;

    partial void OnStatusTextChanged(string value) => StatusKind = StatusKind.None;

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText = text;
        StatusKind = kind;
    }

    /// <summary>Fila seleccionada en cada tabla (object: la tabla también puede seleccionar la fila «nueva» vacía).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveTermCommand))]
    private object? _selectedTerm;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveAddressCommand))]
    private object? _selectedAddress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelRegenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddTermCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddAddressCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveTermCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveAddressCommand))]
    private bool _isBusy;

    private CancellationTokenSource? _regenerateCts;

    /// <summary>Contenido guardado (o cargado), para saber si hay cambios sin guardar.</summary>
    private string _savedSnapshot = "";

    /// <summary>Hay cambios en las tablas respecto a lo último cargado o guardado (las filas vacías no cuentan).</summary>
    public bool HasUnsavedChanges => Snapshot() != _savedSnapshot;

    private string Snapshot()
    {
        static string Clean(string? v) => (v ?? "").Trim();
        var terms = Terms.Select(t => (En: Clean(t.En), Es: Clean(t.Es), Note: Clean(t.Note)))
            .Where(t => t.En.Length > 0 || t.Es.Length > 0 || t.Note.Length > 0)
            .Select(t => string.Join('\u001f', t.En, t.Es, t.Note));
        var address = Address.Select(a => (A: Clean(a.A), B: Clean(a.B), Form: Clean(a.Form), Note: Clean(a.Note)))
            .Where(a => a.A.Length > 0 || a.B.Length > 0 || a.Note.Length > 0)
            .Select(a => string.Join('\u001f', a.A, a.B, a.Form, a.Note));
        return string.Join('\u001e', terms) + '\u001d' + string.Join('\u001e', address);
    }

    private void MarkSaved() => _savedSnapshot = Snapshot();

    /// <summary>Carga el archivo. Devuelve false (y avisa) si existe pero no es válido.</summary>
    public bool Load()
    {
        Glossary? g;
        try
        {
            g = _service.Load(GlossaryPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(ex.Message + "\n\nCorrígelo a mano o usa «Regenerar».", "Glosario no válido");
            Fill(new Glossary());
            MarkSaved();
            SetStatus("El archivo actual no es válido; «Guardar» lo reemplazará.", StatusKind.Warning);
            return true;
        }
        Fill(g ?? new Glossary());
        MarkSaved();
        StatusText = g is null
            ? "Aún no existe: se genera al traducir, con «Regenerar», o puedes escribirlo y «Guardar»."
            : $"{Terms.Count} término(s), {Address.Count} tratamiento(s).";
        return true;
    }

    private void Fill(Glossary g)
    {
        Terms.Clear();
        foreach (var t in g.Terms)
            Terms.Add(new TermRow { En = t.En, Es = t.Es, Note = t.Note });
        Address.Clear();
        foreach (var a in g.Address)
            Address.Add(new AddressRow { A = a.A, B = a.B, Form = a.Form, Note = a.Note });
    }

    /// <summary>Valida las tablas y construye el glosario; las filas totalmente vacías se ignoran.</summary>
    public (Glossary? Glossary, IReadOnlyList<string> Errors) BuildGlossary()
    {
        var errors = new List<string>();
        var g = new Glossary();
        var n = 0;
        foreach (var t in Terms)
        {
            n++;
            var en = (t.En ?? "").Trim();
            var es = (t.Es ?? "").Trim();
            var note = (t.Note ?? "").Trim();
            if (en.Length == 0 && es.Length == 0 && note.Length == 0)
                continue;
            if (en.Length == 0 || es.Length == 0)
                errors.Add($"Término {n}: faltan el texto en inglés o en español.");
            g.Terms.Add(new GlossaryTerm { En = en, Es = es, Note = note });
        }
        n = 0;
        foreach (var a in Address)
        {
            n++;
            var pa = (a.A ?? "").Trim();
            var pb = (a.B ?? "").Trim();
            var form = (a.Form ?? "").Trim();
            var note = (a.Note ?? "").Trim();
            if (pa.Length == 0 && pb.Length == 0 && note.Length == 0)
                continue;
            if (pa.Length == 0 || pb.Length == 0)
                errors.Add($"Tratamiento {n}: faltan los personajes A o B.");
            if (form is not ("tú" or "usted"))
                errors.Add($"Tratamiento {n}: la forma debe ser «tú» o «usted».");
            g.Address.Add(new AddressRule { A = pa, B = pb, Form = form, Note = note });
        }
        var dupes = g.Terms.GroupBy(t => t.En, StringComparer.OrdinalIgnoreCase).Where(x => x.Key.Length > 0 && x.Count() > 1).Select(x => x.Key).ToList();
        if (dupes.Count > 0)
            errors.Add("Términos repetidos: " + string.Join(", ", dupes));

        if (errors.Count == 0)
        {
            // El JSON que se escribirá debe poder leerse igual que al traducir.
            var json = System.Text.Json.JsonSerializer.Serialize(g, TranslationPrompts.IndentedJsonOptions);
            if (TranslationPrompts.ParseGlossary(json) is null)
                errors.Add("El JSON generado no es válido.");
        }
        return (errors.Count == 0 ? g : null, errors);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void Save() => TrySave();

    /// <summary>Valida y guarda. false (con el motivo en <see cref="StatusText"/>) si no se pudo.</summary>
    public bool TrySave()
    {
        var (g, errors) = BuildGlossary();
        if (g is null)
        {
            SetStatus("No se guardó: " + string.Join(" ", errors), StatusKind.Error);
            return false;
        }
        try
        {
            _service.Save(GlossaryPath, g);
            MarkSaved();
            SetStatus($"Guardado ({g.Terms.Count} término(s), {g.Address.Count} tratamiento(s)).", StatusKind.Success);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus("No se pudo guardar el archivo (¿está abierto en otro programa o sin permiso de escritura?): " + ex.Message, StatusKind.Error);
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RegenerateAsync()
    {
        if (!File.Exists(EnglishPath))
        {
            SetStatus("Primero hay que extraer el subtítulo en inglés (procesa el video).", StatusKind.Warning);
            return;
        }
        var s = _settings();
        if (!_dialogs.Confirm($"Se generará de nuevo el glosario con Claude ({Choices.Label(Choices.Backends, s.Backend)}, {Choices.Label(Choices.Models, s.Model)}) " +
                              "y se reemplazará el actual. ¿Continuar?", "Regenerar glosario"))
            return;
        using var cts = new CancellationTokenSource();
        _regenerateCts = cts;
        IsBusy = true;
        SetStatus("Generando glosario… puede tardar unos minutos; las tablas quedan bloqueadas hasta que termine.", StatusKind.InProgress);
        try
        {
            var g = await _service.RegenerateAsync(EnglishPath, s, cts.Token);
            Fill(g);
            MarkSaved();
            SetStatus($"Regenerado y guardado ({g.Terms.Count} término(s), {g.Address.Count} tratamiento(s)).", StatusKind.Success);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Generación cancelada: el glosario no cambió.", StatusKind.Info);
        }
        catch (TranslationClientException ex)
        {
            SetStatus(ex.Message, ex.IsUsageLimit ? StatusKind.Paused : StatusKind.Error);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            SetStatus(ex.Message, StatusKind.Error);
        }
        finally
        {
            _regenerateCts = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void CancelRegenerate()
    {
        // Primero el texto: la cancelación puede terminar la tarea en el acto (y escribir «cancelada»).
        SetStatus("Cancelando…", StatusKind.InProgress);
        _regenerateCts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void AddTerm()
    {
        var row = new TermRow();
        Terms.Add(row);
        SelectedTerm = row;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveTerm))]
    private void RemoveTerm()
    {
        if (SelectedTerm is TermRow row)
            Terms.Remove(row);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void AddAddress()
    {
        var row = new AddressRow();
        Address.Add(row);
        SelectedAddress = row;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveAddress))]
    private void RemoveAddress()
    {
        if (SelectedAddress is AddressRow row)
            Address.Remove(row);
    }

    private bool NotBusy() => !IsBusy;
    private bool CanRemoveTerm() => !IsBusy && SelectedTerm is TermRow;
    private bool CanRemoveAddress() => !IsBusy && SelectedAddress is AddressRow;

    /// <summary>
    /// Al cerrar la ventana. Mientras se regenera, se ofrece cancelar; con cambios sin guardar se pregunta
    /// «¿Guardar?» (Sí guarda y cierra si es válido, No descarta, Cancelar sigue en la ventana).
    /// </summary>
    public bool CanClose()
    {
        if (IsBusy)
        {
            if (!_dialogs.Confirm("Se está generando el glosario. ¿Cancelar la generación y cerrar? El glosario actual no cambiará.",
                    "Regenerar glosario"))
                return false;
            _regenerateCts?.Cancel();
            return true;
        }
        if (!HasUnsavedChanges)
            return true;
        return _dialogs.AskSaveChanges(
            "Hay cambios sin guardar en el glosario. ¿Guardarlos antes de cerrar?\n\nSí: guardar.\nNo: cerrar y perder los cambios.\nCancelar: seguir editando.",
            "Cambios sin guardar") switch
        {
            true => TrySave(),
            false => true,
            null => false,
        };
    }
}
