using System.Text.Json;
using System.Text.Json.Serialization;
using SubtitleTool.Core.Srt;

namespace SubtitleTool.Core.Dubbing;

/// <summary>Emociones que entiende IndexTTS-2.5 (más "neutral"). El orden de <see cref="Vector"/> es el del modelo.</summary>
public static class DubbingEmotions
{
    public const string Neutral = "neutral";

    /// <summary>Orden del vector de IndexTTS-2.5: alegría, ira, tristeza, miedo, asco, melancolía, sorpresa, calma.</summary>
    public static IReadOnlyList<string> Vector { get; } = ["alegria", "ira", "tristeza", "miedo", "asco", "melancolia", "sorpresa", "calma"];

    public static IReadOnlyList<string> All { get; } = [Neutral, .. Vector];

    public static string Label(string emotion) => emotion switch
    {
        "alegria" => "Alegría",
        "ira" => "Ira",
        "tristeza" => "Tristeza",
        "miedo" => "Miedo",
        "asco" => "Asco",
        "melancolia" => "Melancolía",
        "sorpresa" => "Sorpresa",
        "calma" => "Calma",
        _ => "Neutral",
    };

    /// <summary>Normaliza lo que devuelva el modelo ("Ira", "enfado", "angry"…) a una emoción conocida.</summary>
    public static string Normalize(string? value)
    {
        var v = RemoveAccents((value ?? "").Trim().ToLowerInvariant());
        return v switch
        {
            "alegria" or "alegre" or "felicidad" or "feliz" or "happy" or "joy" or "risa" => "alegria",
            "ira" or "enfado" or "enojo" or "rabia" or "furia" or "angry" or "anger" => "ira",
            "tristeza" or "triste" or "sad" or "sadness" => "tristeza",
            "miedo" or "temor" or "panico" or "fear" or "afraid" or "susto" => "miedo",
            "asco" or "desprecio" or "disgust" or "disgusted" or "repugnancia" => "asco",
            "melancolia" or "nostalgia" or "melancholic" => "melancolia",
            "sorpresa" or "asombro" or "surprised" or "surprise" or "sorprendido" => "sorpresa",
            "calma" or "tranquilidad" or "calm" or "sereno" => "calma",
            _ => Neutral,
        };
    }

    internal static string RemoveAccents(string s) =>
        new(s.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());
}

/// <summary>Voces cubanas de Microsoft (edge-tts).</summary>
public static class CubanVoices
{
    public const string Male = "es-CU-ManuelNeural";
    public const string Female = "es-CU-BelkysNeural";

    public static IReadOnlyList<(string Id, string Label)> All { get; } = [(Male, "Manuel (hombre)"), (Female, "Belkys (mujer)")];
}

/// <summary>Una línea a doblar: un bloque del SRT con su personaje y emoción.</summary>
public sealed class DubbingLine
{
    /// <summary>Posición del bloque en el SRT (1-based).</summary>
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("start")] public double Start { get; set; }
    [JsonPropertyName("end")] public double End { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    /// <summary>Personaje; vacío = sin asignar (usa la voz por defecto).</summary>
    [JsonPropertyName("speaker")] public string Speaker { get; set; } = "";
    [JsonPropertyName("emotion")] public string Emotion { get; set; } = DubbingEmotions.Neutral;
    /// <summary>Intensidad de la emoción, 0–1.</summary>
    [JsonPropertyName("intensity")] public double Intensity { get; set; } = 0.6;
}

/// <summary>Un personaje y la voz que se le da.</summary>
public sealed class DubbingCharacter
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>"m", "f" o "?".</summary>
    [JsonPropertyName("gender")] public string Gender { get; set; } = "?";
    [JsonPropertyName("edgeVoice")] public string EdgeVoice { get; set; } = CubanVoices.Male;
    /// <summary>Velocidad de edge-tts en %, p. ej. 15 = "+15%".</summary>
    [JsonPropertyName("rate")] public int Rate { get; set; } = 15;
    /// <summary>Tono de edge-tts en Hz (para distinguir personajes sin muestra de voz).</summary>
    [JsonPropertyName("pitch")] public int Pitch { get; set; }
    /// <summary>Muestra de voz (10–25 s) para Seed-VC; null = sin voz propia.</summary>
    [JsonPropertyName("sample")] public string? SamplePath { get; set; }
}

/// <summary>Proyecto de doblaje de un subtítulo (<c>X.es-cu.doblaje.json</c>, junto al SRT).</summary>
public sealed class DubbingProject
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("subtitles")] public string SubtitlePath { get; set; } = "";
    [JsonPropertyName("annotated")] public bool Annotated { get; set; }
    [JsonPropertyName("lines")] public List<DubbingLine> Lines { get; set; } = [];
    [JsonPropertyName("characters")] public List<DubbingCharacter> Characters { get; set; } = [];

    /// <summary>Ruta del proyecto para un SRT: <c>X.es-cu.srt</c> → <c>X.es-cu.doblaje.json</c>.</summary>
    public static string PathFor(string subtitlePath) =>
        Path.ChangeExtension(subtitlePath, null) + ".doblaje.json";

    /// <summary>Proyecto nuevo a partir del SRT: una línea por bloque con texto, sin personajes.</summary>
    public static DubbingProject FromSrt(string subtitlePath, SrtDocument doc)
    {
        var project = new DubbingProject { SubtitlePath = Path.GetFullPath(subtitlePath) };
        for (var i = 0; i < doc.Blocks.Count; i++)
        {
            var b = doc.Blocks[i];
            if (b.IsEmpty)
                continue;
            project.Lines.Add(new DubbingLine
            {
                Id = i + 1,
                Start = b.Start.TotalSeconds,
                End = b.End.TotalSeconds,
                Text = string.Join("\n", b.Lines),
            });
        }
        return project;
    }

    /// <summary>Copia independiente (para que el motor trabaje sobre ella mientras la ventana sigue viva).</summary>
    public DubbingProject Clone() =>
        JsonSerializer.Deserialize<DubbingProject>(JsonSerializer.Serialize(this))!;

    public DubbingCharacter? Character(string name) =>
        Characters.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Añade los personajes que aparecen en las líneas y no estén en la lista (con una voz según su sexo y un tono
    /// distinto para diferenciarlos) y quita los que ya no tienen líneas. Devuelve cuántos se añadieron.
    /// </summary>
    public int SyncCharacters(IReadOnlyDictionary<string, string>? genders = null)
    {
        var used = Lines.Select(l => l.Speaker).Where(s => !string.IsNullOrWhiteSpace(s))
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.First())
            .ToList();
        Characters.RemoveAll(c => !used.Contains(c.Name, StringComparer.OrdinalIgnoreCase));
        var added = 0;
        foreach (var name in used)
        {
            if (Character(name) is not null)
                continue;
            var gender = genders is not null && genders.TryGetValue(name, out var g) ? g : "?";
            var voice = gender == "f" ? CubanVoices.Female : CubanVoices.Male;
            // Personajes con la misma voz base: se alterna el tono para que no suenen iguales.
            var sameVoice = Characters.Count(c => c.EdgeVoice == voice);
            Characters.Add(new DubbingCharacter
            {
                Name = name,
                Gender = gender,
                EdgeVoice = voice,
                Pitch = PitchSteps[sameVoice % PitchSteps.Length],
            });
            added++;
        }
        // Orden: los que más hablan primero.
        var order = used.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.OrdinalIgnoreCase);
        Characters.Sort((a, b) => order[a.Name].CompareTo(order[b.Name]));
        return added;
    }

    private static readonly int[] PitchSteps = [0, -15, 12, -28, 24, -8, 6];
}

/// <summary>Lectura y guardado de proyectos de doblaje.</summary>
public static class DubbingProjectStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Proyecto guardado del SRT (o uno nuevo si no hay o está dañado). Personaje y emoción se conservan en las líneas que
    /// siguen siendo la misma: mismo texto, o mismo número de bloques y mismo inicio (texto corregido). Si se fusionan o
    /// parten bloques, las líneas desplazadas no heredan etiquetas ajenas.
    /// </summary>
    public static DubbingProject LoadOrCreate(string subtitlePath)
    {
        var doc = SrtParser.ParseFile(subtitlePath);
        var fresh = DubbingProject.FromSrt(subtitlePath, doc);
        var path = DubbingProject.PathFor(subtitlePath);
        if (!File.Exists(path))
            return fresh;
        DubbingProject? saved;
        try
        {
            saved = JsonSerializer.Deserialize<DubbingProject>(File.ReadAllText(path), Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException)
        {
            return fresh;
        }
        if (saved?.Lines is null)
            return fresh;
        // El texto y los tiempos mandan desde el SRT (pudo editarse después).
        var byId = saved.Lines.Where(l => l is not null).GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First());
        var sameShape = saved.Lines.Count == fresh.Lines.Count;
        foreach (var line in fresh.Lines)
        {
            if (!byId.TryGetValue(line.Id, out var old))
                continue;
            var same = old.Text == line.Text || (sameShape && Math.Abs(old.Start - line.Start) < 0.5);
            if (!same)
                continue;
            line.Speaker = old.Speaker ?? "";
            line.Emotion = old.Emotion is not null && DubbingEmotions.All.Contains(old.Emotion) ? old.Emotion : DubbingEmotions.Neutral;
            line.Intensity = Math.Clamp(old.Intensity, 0, 1);
        }
        fresh.Characters = (saved.Characters ?? []).Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Name)).ToList();
        fresh.Annotated = saved.Annotated;
        fresh.SyncCharacters();
        return fresh;
    }

    public static void Save(DubbingProject project)
    {
        var path = DubbingProject.PathFor(project.SubtitlePath);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(project, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
