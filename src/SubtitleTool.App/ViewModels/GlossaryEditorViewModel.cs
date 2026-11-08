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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateCommand))]
    private bool _isBusy;

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
            StatusText = "El archivo actual no es válido; «Guardar» lo reemplazará.";
            return true;
        }
        Fill(g ?? new Glossary());
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
    private void Save()
    {
        var (g, errors) = BuildGlossary();
        if (g is null)
        {
            StatusText = "No se guardó: " + string.Join(" ", errors);
            return;
        }
        try
        {
            _service.Save(GlossaryPath, g);
            StatusText = $"Guardado ({g.Terms.Count} término(s), {g.Address.Count} tratamiento(s)).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = "No se pudo guardar: " + ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RegenerateAsync()
    {
        if (!File.Exists(EnglishPath))
        {
            StatusText = "Primero hay que extraer el subtítulo en inglés (procesa el video).";
            return;
        }
        var s = _settings();
        if (!_dialogs.Confirm($"Se generará de nuevo el glosario con Claude ({Choices.Label(Choices.Backends, s.Backend)}, {Choices.Label(Choices.Models, s.Model)}) " +
                              "y se reemplazará el actual. ¿Continuar?", "Regenerar glosario"))
            return;
        IsBusy = true;
        StatusText = "Generando glosario…";
        try
        {
            var g = await _service.RegenerateAsync(EnglishPath, s, CancellationToken.None);
            Fill(g);
            StatusText = $"Regenerado y guardado ({g.Terms.Count} término(s), {g.Address.Count} tratamiento(s)).";
        }
        catch (TranslationClientException ex)
        {
            StatusText = (ex.IsUsageLimit ? "⏸️ " : "❌ ") + ex.Message;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            StatusText = "❌ " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool NotBusy() => !IsBusy;
}
