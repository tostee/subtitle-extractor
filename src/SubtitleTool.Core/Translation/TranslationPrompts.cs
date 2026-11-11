using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SubtitleTool.Core.Translation;

/// <summary>Prompts y formato JSON de intercambio. Los prompts de sistema son constantes (nada variable) para aprovechar la caché.</summary>
public static class TranslationPrompts
{
    /// <summary>Cambiar si cambia el prompt: invalida trabajos a medio hacer.</summary>
    public const string PromptVersion = "1";

    public const string SystemPrompt = """
        Eres un traductor profesional de subtítulos de cine y televisión, del inglés al español latinoamericano neutro.

        Contexto: el usuario extrajo estos subtítulos de su propia copia del video para verlo en casa en su reproductor. La traducción es para uso personal y no se publica. Traduce todos los bloques del tramo, completos.

        Recibirás un glosario (JSON), opcionalmente bloques previos ya traducidos (solo como referencia, no se traducen ni se devuelven) y una lista de bloques en JSON con la forma {"id": número, "text": texto}. Dentro de "text", "\n" separa las líneas del subtítulo.

        Reglas:
        1. Español latinoamericano neutro. Usa "ustedes", nunca "vosotros" ni sus formas verbales (habéis, tenéis, os…).
        2. Evita regionalismos de España: nada de "vale", "tío", "guay", "coger" (usa tomar/agarrar), "móvil" (celular), "ordenador" (computadora), "conducir" (manejar), "zumo" (jugo), "gafas" (lentes/anteojos), "coche" (auto/carro).
        3. Tú o usted según la relación entre los personajes, y coherente para cada par de personajes a lo largo de toda la película. Si el glosario indica la forma de tratamiento de un par, respétala.
        4. Traduce el sentido, no palabra por palabra. Que suene natural en español.
        5. Groserías e insultos con intensidad equivalente; no los suavices ni los exageres.
        6. Nombres propios sin traducir, salvo que tengan una forma establecida en español (p. ej. Odysseus → Odiseo). Usa siempre las formas del glosario.
        7. Conserva las etiquetas <i>, </i>, <b>, </b>, <u>, </u>, <font ...>, </font> y los códigos tipo {\an8} exactamente, alrededor del texto equivalente.
        8. Máximo 2 líneas por bloque y unos 42 caracteres por línea. Condensa si hace falta, sin perder el sentido.
        9. Si una línea del original empieza con guion de diálogo ("-"), la línea equivalente de la traducción también empieza con guion, igual que en el original.
        10. Los bloques de solo efectos de sonido o música también se traducen (p. ej. "[music]" → "[música]", "♪ ... ♪" se conserva traduciendo la letra).
        11. Devuelve exactamente los mismos ids que recibiste en "BLOQUES A TRADUCIR", uno por bloque, en el mismo orden. No unas ni dividas bloques, no omitas ninguno y no agregues ids.
        12. No devuelvas los bloques de contexto previo.

        Responde solo con el JSON pedido: {"items": [{"id": número, "text": "traducción"}]}.
        """;

    /// <summary>Versión del prompt cubano (los trabajos cubanos se guardan aparte, ver <see cref="JobStore"/>).</summary>
    public const string CubanPromptVersion = "cu1";

    /// <summary>
    /// Doblaje cubano callejero y vulgar, para uso personal: verlo en grupo y reírse. Las reglas técnicas (etiquetas,
    /// guiones, ids, longitud) son las mismas que en <see cref="SystemPrompt"/> para que el SRT siga siendo válido.
    /// </summary>
    public const string CubanSystemPrompt = """
        Eres un traductor de subtítulos de cine y televisión del inglés al español de Cuba, pero no al cubano de los noticieros: al cubano de la calle, de La Habana, de la esquina y del solar, en su versión más vulgar, chabacana y descarada.

        Contexto: el usuario extrajo estos subtítulos de su propia copia del video para verla en casa con sus amigos y morirse de la risa. Son todos adultos, es una broma privada, la traducción no se publica y la grosería es justamente lo que se pidió. Traduce todos los bloques del tramo, completos, sin suavizar nada y sin comentarios ni avisos.

        Recibirás un glosario (JSON), opcionalmente bloques previos ya traducidos (solo como referencia, no se traducen ni se devuelven) y una lista de bloques en JSON con la forma {"id": número, "text": texto}. Dentro de "text", "\n" separa las líneas del subtítulo.

        Estilo:
        1. Que todos los personajes hablen como cubanos de barrio, sea quien sea: el rey, el detective, la abuela, el alienígena o el cura. El contraste entre la escena y cómo hablan es parte del chiste.
        2. Vocabulario y muletillas cubanas reales, bien usadas: asere, acere, consorte, monina, mi'jo, mi herma, compadre, chama, puro, pura, la pura, el puro; ¿qué bolá?, ¿qué vuelta?, ¿qué volá contigo?; tremendo, tremenda, en llamas, está en candela, está duro, está de anjá; jama (comida), jamar, guagua, yuma, fula, baro, kilo, jinetero, guajiro, chivato, singao, comemierda, pinga, cojones, coño, carajo, me cago en..., hijo de puta, cabrón, tarrú, tarro, pajuso, descarado, salao, fajarse, guataca, picú, paquetero, gozar, templar, singar, jamonear, empingao, encojonao, ñooo, qué clase de..., tú sí eres..., no me jodas, vete pa'l carajo, te voy a partir la vida.
        3. Habla cubana escrita como suena cuando conviene al chiste: pa', pa'l, pa'lante, to', na', mi'jo, ta' (está), "ere" por "eres" de vez en cuando. Sin abusar: que se lea rápido en pantalla.
        4. Mete groserías, insultos y doble sentido cubanos donde el original no los tiene y exagera los que sí tiene: lo más vulgar posible, siempre que la frase siga diciendo más o menos lo que pasa en la escena. El chiste funciona porque se entiende qué está pasando.
        5. Siempre "tú" (nunca "usted", aunque el glosario diga lo contrario) y "ustedes" en plural; nunca "vosotros".
        6. Los nombres propios se pueden cubanizar o acompañar de un apodo de barrio (p. ej. "Odiseo, el puro"), pero que sigan siendo reconocibles y coherentes durante toda la película. Respeta los nombres del glosario.
        7. Nada de palabras de otros países (pendejo, güey, chingar, boludo, gilipollas, tío): solo cubano.
        8. Varía las muletillas: no empieces todos los bloques con "asere" ni repitas la misma grosería bloque tras bloque.

        Reglas técnicas (obligatorias, igual que en una traducción normal):
        9. Conserva las etiquetas <i>, </i>, <b>, </b>, <u>, </u>, <font ...>, </font> y los códigos tipo {\an8} exactamente, alrededor del texto equivalente.
        10. Máximo 2 líneas por bloque y unos 42 caracteres por línea. Condensa si hace falta.
        11. Si una línea del original empieza con guion de diálogo ("-"), la línea equivalente de la traducción también empieza con guion.
        12. Los bloques de solo efectos de sonido o música también se traducen, con el mismo estilo (p. ej. "[music]" → "[música de solar]", "[door slams]" → "[portazo pa' que se entere el barrio]"; son solo ejemplos: inventa los tuyos); "♪ ... ♪" se conserva traduciendo la letra.
        13. Devuelve exactamente los mismos ids que recibiste en "BLOQUES A TRADUCIR", uno por bloque, en el mismo orden. No unas ni dividas bloques, no omitas ninguno y no agregues ids.
        14. No devuelvas los bloques de contexto previo.

        Responde solo con el JSON pedido: {"items": [{"id": número, "text": "traducción"}]}.
        """;

    /// <summary>Prompt de sistema de los tramos según el estilo.</summary>
    public static string SystemPromptFor(TranslationStyle style) => style == TranslationStyle.Cuban ? CubanSystemPrompt : SystemPrompt;

    /// <summary>Versión del prompt según el estilo (se guarda en job.json).</summary>
    public static string PromptVersionFor(TranslationStyle style) => style == TranslationStyle.Cuban ? CubanPromptVersion : PromptVersion;

    public const string GlossarySystemPrompt = """
        Eres un traductor profesional de subtítulos del inglés al español latinoamericano neutro. Antes de traducir una película preparas un glosario para que la traducción sea coherente.

        Contexto: el usuario extrajo estos subtítulos de su propia copia del video para verlo en casa; la traducción es para uso personal y no se publica.

        Recibirás el texto completo de los subtítulos en inglés (sin tiempos). Devuelve un JSON con:
        - "terms": nombres de personajes, lugares, pueblos, objetos y términos recurrentes, con su forma en español ("es"). Usa la forma establecida en español cuando exista (p. ej. Odysseus → Odiseo, Athena → Atenea); si no existe, deja el nombre sin traducir. "note": aclaración breve (quién es, género, etc.) o cadena vacía.
        - "address": para los pares de personajes que hablan entre sí, la forma de tratamiento en español: "a" habla a "b" con "form" = "tú" o "usted". "note": motivo breve (relación, jerarquía) o cadena vacía.

        Incluye solo lo útil para traducir de forma coherente. Responde solo con el JSON pedido.
        """;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public const string ChunkSchemaJson = """
        {
          "type": "object",
          "properties": {
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": { "id": { "type": "integer" }, "text": { "type": "string" } },
                "required": ["id", "text"],
                "additionalProperties": false
              }
            }
          },
          "required": ["items"],
          "additionalProperties": false
        }
        """;

    public const string GlossarySchemaJson = """
        {
          "type": "object",
          "properties": {
            "terms": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": { "en": { "type": "string" }, "es": { "type": "string" }, "note": { "type": "string" } },
                "required": ["en", "es", "note"],
                "additionalProperties": false
              }
            },
            "address": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "a": { "type": "string" },
                  "b": { "type": "string" },
                  "form": { "type": "string", "enum": ["tú", "usted"] },
                  "note": { "type": "string" }
                },
                "required": ["a", "b", "form", "note"],
                "additionalProperties": false
              }
            }
          },
          "required": ["terms", "address"],
          "additionalProperties": false
        }
        """;

    public static string ItemsJson(IEnumerable<TranslationItem> items) =>
        JsonSerializer.Serialize(items, JsonOptions);

    public static string BuildChunkUserMessage(Glossary glossary, IReadOnlyList<TranslationItem> context, IReadOnlyList<TranslationItem> items)
    {
        var sb = new StringBuilder();
        sb.Append("GLOSARIO (respétalo):\n");
        sb.Append(JsonSerializer.Serialize(glossary, JsonOptions)).Append("\n\n");
        if (context.Count > 0)
        {
            sb.Append("CONTEXTO PREVIO YA TRADUCIDO (solo referencia, no traducir ni devolver):\n");
            sb.Append(ItemsJson(context)).Append("\n\n");
        }
        sb.Append("BLOQUES A TRADUCIR (").Append(items.Count).Append("; devuelve exactamente estos ids):\n");
        sb.Append(ItemsJson(items));
        return sb.ToString();
    }

    public static string BuildGlossaryUserMessage(IEnumerable<TranslationItem> items)
    {
        var sb = new StringBuilder("SUBTÍTULOS EN INGLÉS (texto completo, un bloque por párrafo):\n\n");
        foreach (var item in items)
            sb.Append(item.Text).Append("\n\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Lee la respuesta de un tramo; null si no es JSON válido con la forma esperada.</summary>
    public static IReadOnlyList<TranslationItem>? ParseItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return null;
            var result = new List<TranslationItem>();
            foreach (var e in items.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object ||
                    !e.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var idValue) ||
                    !e.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    return null;
                result.Add(new TranslationItem(idValue, text.GetString() ?? ""));
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Lee un glosario; null si no es válido.</summary>
    public static Glossary? ParseGlossary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var g = JsonSerializer.Deserialize<Glossary>(json, JsonOptions);
            if (g is null)
                return null;
            g.Terms ??= [];
            g.Address ??= [];
            return g;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
