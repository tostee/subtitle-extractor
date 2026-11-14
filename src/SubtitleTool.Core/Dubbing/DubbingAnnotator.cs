using System.Globalization;
using System.Text;
using System.Text.Json;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Dubbing;

/// <summary>Progreso del etiquetado: líneas hechas de un total.</summary>
public sealed record AnnotationProgress(int Done, int Total, TokenUsage Usage);

public sealed class AnnotationException : Exception
{
    public AnnotationException(string message) : base(message) { }
}

/// <summary>
/// Pide a Claude, por tramos, quién dice cada línea, su sexo y con qué emoción. Reutiliza el cliente de traducción:
/// el campo "text" de la respuesta lleva <c>personaje | sexo | emoción | intensidad</c>.
/// </summary>
public sealed class DubbingAnnotator
{
    public const int DefaultChunkSize = 120;

    public const string SystemPrompt = """
        Eres un director de doblaje. Recibes los subtítulos de una película (en español, a veces en cubano vulgar) y para cada bloque decides quién lo dice y con qué emoción, para darle voz.

        Para cada bloque devuelve en "text" exactamente: personaje | sexo | emoción | intensidad
        - personaje: nombre del personaje que habla, tal y como lo llamaría el público (p. ej. "Odiseo", "Penélope"). Si no se sabe su nombre, una descripción corta y estable ("Soldado 1", "Mujer del mercado", "Narrador"). Usa siempre el mismo nombre para el mismo personaje en toda la película (respeta la lista de PERSONAJES YA IDENTIFICADOS).
        - sexo: m (hombre), f (mujer) o ? (no se sabe).
        - emoción: una de neutral, alegria, ira, tristeza, miedo, asco, melancolia, sorpresa, calma.
        - intensidad: de 0.1 a 1.0 (0.3 leve, 0.6 clara, 0.9 extrema).

        Deduce el personaje por el contexto: quién responde a quién, nombres que se mencionan, el glosario. En un diálogo con guiones ("-Hola. -Adiós.") elige al que dice la primera frase.
        Si el bloque no es diálogo (efectos de sonido entre corchetes, letras de canciones, carteles), usa personaje "-", sexo ?, emoción neutral.
        Ejemplo: {"id": 12, "text": "Odiseo | m | ira | 0.8"}

        Devuelve exactamente los mismos ids que recibes, uno por bloque. Responde solo con el JSON pedido: {"items": [{"id": número, "text": "..."}]}.
        """;

    private readonly ITranslationClient _client;
    private readonly int _chunkSize;

    public DubbingAnnotator(ITranslationClient client, int chunkSize = DefaultChunkSize)
    {
        _client = client;
        _chunkSize = Math.Max(10, chunkSize);
    }

    /// <summary>Etiquetas leídas de una línea de la respuesta.</summary>
    public sealed record Annotation(string Speaker, string Gender, string Emotion, double Intensity);

    /// <summary>Lee "personaje | sexo | emoción | intensidad"; null si no tiene esa forma.</summary>
    public static Annotation? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var parts = text.Split('|').Select(p => p.Trim()).ToArray();
        if (parts.Length < 3)
            return null;
        var speaker = parts[0].Trim('"', '\'', '*').Trim();
        if (speaker is "-" or "—" or "")
            speaker = "";
        var gender = parts[1].ToLowerInvariant() switch
        {
            "m" or "h" or "hombre" or "masculino" or "male" => "m",
            "f" or "mujer" or "femenino" or "female" => "f",
            _ => "?",
        };
        var emotion = DubbingEmotions.Normalize(parts[2]);
        var intensity = 0.6;
        if (parts.Length > 3 && double.TryParse(parts[3].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            intensity = Math.Clamp(v, 0.1, 1.0);
        return new Annotation(speaker, gender, emotion, intensity);
    }

    /// <summary>
    /// Etiqueta todas las líneas del proyecto (personaje, emoción e intensidad), crea la lista de personajes con una voz
    /// según su sexo y marca el proyecto como etiquetado. <paramref name="glossary"/> ayuda con los nombres.
    /// </summary>
    /// <exception cref="AnnotationException">El modelo se negó o la respuesta no se pudo leer tras reintentar.</exception>
    public async Task<TokenUsage> AnnotateAsync(DubbingProject project, Glossary? glossary, IProgress<AnnotationProgress>? progress,
        CancellationToken ct)
    {
        var usage = TokenUsage.Zero;
        var genderVotes = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var lines = project.Lines;
        var done = 0;
        progress?.Report(new AnnotationProgress(0, lines.Count, usage));
        for (var start = 0; start < lines.Count; start += _chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = lines.Skip(start).Take(_chunkSize).ToList();
            var context = lines.Skip(Math.Max(0, start - 8)).Take(Math.Min(8, start)).ToList();
            var pending = chunk;
            for (var attempt = 0; attempt < 3 && pending.Count > 0; attempt++)
            {
                var request = new ChunkRequest
                {
                    ChunkNumber = start / _chunkSize + 1,
                    SystemPrompt = SystemPrompt,
                    UserMessage = BuildUserMessage(glossary, genderVotes.Keys, context, pending),
                    Items = pending.Select(l => new TranslationItem(l.Id, l.Text)).ToList(),
                };
                var response = await _client.TranslateChunkAsync(request, ct).ConfigureAwait(false);
                usage = usage.Add(response.Usage);
                if (response.StopReason == StopReasons.Refusal)
                    throw new AnnotationException("Claude se negó a etiquetar un tramo" +
                        (string.IsNullOrWhiteSpace(response.RefusalExplanation) ? "." : ": " + response.RefusalExplanation));
                var parsed = (response.Items ?? [])
                    .Select(i => (i.Id, Note: Parse(i.Text)))
                    .Where(x => x.Note is not null)
                    .GroupBy(x => x.Id)
                    .ToDictionary(g => g.Key, g => g.First().Note!);
                foreach (var line in pending)
                {
                    if (!parsed.TryGetValue(line.Id, out var a))
                        continue;
                    line.Speaker = a.Speaker;
                    line.Emotion = a.Emotion;
                    line.Intensity = a.Intensity;
                    if (a.Speaker.Length > 0 && a.Gender != "?")
                    {
                        if (!genderVotes.TryGetValue(a.Speaker, out var votes))
                            genderVotes[a.Speaker] = votes = [];
                        votes[a.Gender] = votes.GetValueOrDefault(a.Gender) + 1;
                    }
                    else if (a.Speaker.Length > 0 && !genderVotes.ContainsKey(a.Speaker))
                    {
                        genderVotes[a.Speaker] = [];
                    }
                }
                pending = pending.Where(l => !parsed.ContainsKey(l.Id)).ToList();
            }
            if (pending.Count == chunk.Count)
                throw new AnnotationException($"No se pudo leer la respuesta de Claude para las líneas {chunk[0].Id}–{chunk[^1].Id}.");
            done += chunk.Count;
            progress?.Report(new AnnotationProgress(done, lines.Count, usage));
        }

        var genders = genderVotes.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Count == 0 ? "?" : kv.Value.MaxBy(v => v.Value).Key,
            StringComparer.OrdinalIgnoreCase);
        // Los personajes que ya tenían voz la conservan; para los demás se elige según el sexo detectado.
        foreach (var c in project.Characters)
            if (c.Gender == "?" && genders.TryGetValue(c.Name, out var g))
                c.Gender = g;
        project.SyncCharacters(genders);
        project.Annotated = true;
        return usage;
    }

    public static string BuildUserMessage(Glossary? glossary, IEnumerable<string> knownCharacters,
        IReadOnlyList<DubbingLine> context, IReadOnlyList<DubbingLine> items)
    {
        var sb = new StringBuilder();
        var names = glossary?.Terms.Where(t => !string.IsNullOrWhiteSpace(t.Es))
            .Select(t => string.IsNullOrWhiteSpace(t.Note) ? t.Es : $"{t.Es} ({t.Note})").ToList() ?? [];
        if (names.Count > 0)
            sb.Append("GLOSARIO (nombres y notas):\n").Append(string.Join("; ", names)).Append("\n\n");
        var known = knownCharacters.ToList();
        if (known.Count > 0)
            sb.Append("PERSONAJES YA IDENTIFICADOS (reutiliza estos nombres):\n").Append(string.Join("; ", known)).Append("\n\n");
        if (context.Count > 0)
        {
            sb.Append("CONTEXTO PREVIO (ya etiquetado, solo referencia):\n");
            foreach (var l in context)
                sb.Append(l.Speaker.Length > 0 ? l.Speaker : "?").Append(": ").Append(l.Text.Replace('\n', ' ')).Append('\n');
            sb.Append('\n');
        }
        sb.Append("BLOQUES A ETIQUETAR (").Append(items.Count).Append("; devuelve exactamente estos ids):\n");
        sb.Append(JsonSerializer.Serialize(items.Select(l => new TranslationItem(l.Id, l.Text)), TranslationPrompts.JsonOptions));
        return sb.ToString();
    }
}
