using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace SubtitleTool.Core.Translation;

/// <summary>Cliente real con el SDK oficial de Anthropic. El SDK ya reintenta 408/409/429/5xx; aquí no se duplica.</summary>
public sealed class ClaudeTranslationClient : ITranslationClient
{
    public const int ChunkMaxTokens = 16000;
    public const int GlossaryMaxTokens = 16000;

    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly Effort _effort;

    private static readonly Dictionary<string, JsonElement> ChunkSchema = ToSchema(TranslationPrompts.ChunkSchemaJson);
    private static readonly Dictionary<string, JsonElement> GlossarySchema = ToSchema(TranslationPrompts.GlossarySchemaJson);

    public ClaudeTranslationClient(string apiKey, string model, string effort)
    {
        ModelPricing.For(model); // valida el modelo
        _model = model;
        _effort = ParseEffort(effort);
        _client = new AnthropicClient { ApiKey = apiKey };
    }

    public static Effort ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        _ => throw new ArgumentException($"Esfuerzo no válido: '{effort}'. Usa low, medium o high.", nameof(effort)),
    };

    public async Task<ChunkResponse> TranslateChunkAsync(ChunkRequest request, CancellationToken cancellationToken = default)
    {
        var response = await CreateAsync(request.SystemPrompt, request.UserMessage, ChunkSchema, ChunkMaxTokens, cancellationToken).ConfigureAwait(false);
        var stop = StopReasonOf(response);
        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        string? category = null, explanation = null;
        if (stop == StopReasons.Refusal && response.StopDetails is { } details)
        {
            category = details.Category?.Raw();
            explanation = details.Explanation;
        }
        return new ChunkResponse
        {
            Items = stop == StopReasons.EndTurn ? TranslationPrompts.ParseItems(text) : null,
            StopReason = stop,
            RefusalCategory = category,
            RefusalExplanation = explanation,
            Usage = UsageOf(response),
        };
    }

    public async Task<GlossaryResponse> BuildGlossaryAsync(GlossaryRequest request, CancellationToken cancellationToken = default)
    {
        var response = await CreateAsync(request.SystemPrompt, request.UserMessage, GlossarySchema, GlossaryMaxTokens, cancellationToken).ConfigureAwait(false);
        var stop = StopReasonOf(response);
        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        var glossary = stop == StopReasons.EndTurn ? TranslationPrompts.ParseGlossary(text) : null;
        return new GlossaryResponse(glossary, stop, UsageOf(response));
    }

    public async Task<long> CountTokensAsync(string? systemPrompt, string userMessage, CancellationToken cancellationToken = default)
    {
        var p = new MessageCountTokensParams
        {
            Model = _model,
            Messages = [new UserMessageParam { Content = userMessage }],
        };
        if (systemPrompt is not null)
            p = p with { System = new List<TextBlockParam> { new() { Text = systemPrompt } } };
        try
        {
            var result = await _client.Messages.CountTokens(p, cancellationToken).ConfigureAwait(false);
            return result.InputTokens;
        }
        catch (AnthropicException ex)
        {
            throw Wrap(ex);
        }
    }

    private async Task<Message> CreateAsync(string system, string user, Dictionary<string, JsonElement> schema, int maxTokens, CancellationToken ct)
    {
        var p = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = maxTokens,
            System = new List<TextBlockParam> { new() { Text = system, CacheControl = new CacheControlEphemeral() } },
            Messages = [new UserMessageParam { Content = user }],
            OutputConfig = new OutputConfig
            {
                Effort = _effort,
                Format = new JsonOutputFormat { Schema = schema },
            },
        };
        try
        {
            return await _client.Messages.Create(p, ct).ConfigureAwait(false);
        }
        catch (AnthropicException ex)
        {
            throw Wrap(ex);
        }
    }

    /// <summary>Valor crudo de la API ("end_turn", "max_tokens", "refusal"…).</summary>
    private static string StopReasonOf(Message m) => m.StopReason?.Raw() ?? "";

    private static TokenUsage UsageOf(Message m) => new(
        m.Usage.InputTokens,
        m.Usage.OutputTokens,
        m.Usage.CacheReadInputTokens ?? 0,
        m.Usage.CacheCreationInputTokens ?? 0);

    private static TranslationClientException Wrap(AnthropicException ex) => ex switch
    {
        AnthropicUnauthorizedException => new("La API rechazó la clave (401): revisa la clave con 'subtool key status' o configúrala con 'subtool key set'.", ex, isAuthentication: true),
        AnthropicBadRequestException => new($"Petición rechazada por la API (400): {ex.Message}", ex),
        AnthropicNotFoundException => new($"Recurso o modelo no encontrado (404): {ex.Message}", ex),
        AnthropicRateLimitException => new("Límite de peticiones de la API alcanzado (429) tras los reintentos del SDK. Espera y relanza: el progreso queda guardado.", ex),
        Anthropic5xxException => new($"Error del servidor de la API (5xx) tras los reintentos del SDK: {ex.Message}", ex),
        AnthropicIOException => new($"Error de red al llamar a la API: {ex.Message}", ex),
        _ => new($"Error de la API: {ex.Message}", ex),
    };

    private static Dictionary<string, JsonElement> ToSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}
