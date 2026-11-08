namespace SubtitleTool.Core.Translation;

/// <summary>Precios en USD por millón de tokens.</summary>
public sealed record ModelPricing(string Model, decimal InputPerMTok, decimal OutputPerMTok, decimal CacheReadPerMTok, decimal CacheWritePerMTok)
{
    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>Escritura en caché (5 min) = 1,25 × entrada; lectura = 5 % de la entrada (Opus: $0,20 sobre $4).</summary>
    private static ModelPricing Create(string model, decimal input, decimal output) =>
        new(model, input, output, input * 0.05m, input * 1.25m);

    public static readonly IReadOnlyDictionary<string, ModelPricing> Known = new Dictionary<string, ModelPricing>(StringComparer.Ordinal)
    {
        ["claude-opus-5-5"] = Create("claude-opus-5-5", 4m, 20m),
        ["claude-sonnet-5-5"] = Create("claude-sonnet-5-5", 2m, 10m),
        ["claude-haiku-5-5"] = Create("claude-haiku-5-5", 0.10m, 0.50m),
    };

    public static ModelPricing For(string model) =>
        Known.TryGetValue(model, out var p)
            ? p
            : throw new ArgumentException($"Modelo no soportado: '{model}'. Usa: {string.Join(", ", Known.Keys)}.", nameof(model));

    /// <summary>Coste de un uso real (InputTokens de la API no incluye los tokens leídos/escritos en caché).</summary>
    public decimal Cost(TokenUsage usage) =>
        (usage.InputTokens * InputPerMTok
         + usage.OutputTokens * OutputPerMTok
         + usage.CacheReadInputTokens * CacheReadPerMTok
         + usage.CacheCreationInputTokens * CacheWritePerMTok) / 1_000_000m;
}

public sealed record CostEstimate
{
    public required string Model { get; init; }
    public int ChunkCount { get; init; }
    public long SystemPromptTokens { get; init; }
    /// <summary>Tokens de entrada totales de todas las llamadas (incluido el prompt de sistema en cada una).</summary>
    public long TotalInputTokens { get; init; }
    public long EnglishTextTokens { get; init; }
    public long EstimatedOutputTokens { get; init; }
    public long GlossaryInputTokens { get; init; }
    public decimal InputCostUsd { get; init; }
    public decimal OutputCostUsd { get; init; }
    public decimal TotalCostUsd => InputCostUsd + OutputCostUsd;
}

public static class CostEstimator
{
    public const decimal OutputFactor = 1.3m;
    /// <summary>Salida aproximada de la pasada de glosario.</summary>
    public const long GlossaryOutputTokens = 3000;

    /// <summary>
    /// Estima el coste: cada tramo paga su entrada; el prompt de sistema se escribe en caché en la primera llamada
    /// y se lee de caché en las siguientes. Salida ≈ 1,3 × tokens del texto inglés (+ glosario si hay que generarlo).
    /// </summary>
    /// <param name="chunkInputTokens">Tokens de entrada de cada tramo, incluido el prompt de sistema.</param>
    /// <param name="glossaryInputTokens">0 si el glosario ya existe.</param>
    public static CostEstimate Estimate(string model, long systemPromptTokens, IReadOnlyList<long> chunkInputTokens, long englishTextTokens, long glossaryInputTokens)
    {
        var pricing = ModelPricing.For(model);
        decimal inputCost = 0;
        for (var i = 0; i < chunkInputTokens.Count; i++)
        {
            var uncached = Math.Max(0, chunkInputTokens[i] - systemPromptTokens);
            inputCost += uncached * pricing.InputPerMTok
                         + systemPromptTokens * (i == 0 ? pricing.CacheWritePerMTok : pricing.CacheReadPerMTok);
        }
        inputCost += glossaryInputTokens * pricing.InputPerMTok;

        var output = (long)Math.Ceiling(englishTextTokens * OutputFactor) + (glossaryInputTokens > 0 ? GlossaryOutputTokens : 0);
        return new CostEstimate
        {
            Model = model,
            ChunkCount = chunkInputTokens.Count,
            SystemPromptTokens = systemPromptTokens,
            TotalInputTokens = chunkInputTokens.Sum() + glossaryInputTokens,
            EnglishTextTokens = englishTextTokens,
            EstimatedOutputTokens = output,
            GlossaryInputTokens = glossaryInputTokens,
            InputCostUsd = inputCost / 1_000_000m,
            OutputCostUsd = output * pricing.OutputPerMTok / 1_000_000m,
        };
    }
}
