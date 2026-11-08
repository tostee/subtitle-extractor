using System.Text.Json.Serialization;
using SubtitleTool.Core.Srt;

namespace SubtitleTool.Core.Translation;

/// <summary>Elemento de intercambio con el modelo: id del bloque (posición 1-based) y texto (líneas unidas con \n). Nunca tiempos.</summary>
public sealed record TranslationItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("text")] string Text);

/// <summary>Uso de tokens de una o varias llamadas.</summary>
public sealed record TokenUsage(long InputTokens, long OutputTokens, long CacheReadInputTokens, long CacheCreationInputTokens)
{
    public static readonly TokenUsage Zero = new(0, 0, 0, 0);

    /// <summary>Coste que informa el propio backend (Claude Code: total_cost_usd, equivalente API); 0 si no lo informa.</summary>
    public decimal ReportedCostUsd { get; init; }

    public TokenUsage Add(TokenUsage other) => new(
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        CacheReadInputTokens + other.CacheReadInputTokens,
        CacheCreationInputTokens + other.CacheCreationInputTokens)
    {
        ReportedCostUsd = ReportedCostUsd + other.ReportedCostUsd,
    };
}

/// <summary>Petición de traducción de un tramo (o de parte de él).</summary>
public sealed record ChunkRequest
{
    public required int ChunkNumber { get; init; }
    /// <summary>Prompt de sistema (constante, para que la caché funcione).</summary>
    public required string SystemPrompt { get; init; }
    /// <summary>Mensaje de usuario completo (glosario + contexto + bloques).</summary>
    public required string UserMessage { get; init; }
    /// <summary>Bloques que se piden traducir.</summary>
    public required IReadOnlyList<TranslationItem> Items { get; init; }
    /// <summary>Bloques ya traducidos que se envían solo como referencia.</summary>
    public IReadOnlyList<TranslationItem> Context { get; init; } = [];
}

/// <summary>Respuesta de un tramo. <see cref="Items"/> es null si la respuesta no era JSON válido.</summary>
public sealed record ChunkResponse
{
    public IReadOnlyList<TranslationItem>? Items { get; init; }
    /// <summary>"end_turn", "max_tokens", "refusal"…</summary>
    public string StopReason { get; init; } = StopReasons.EndTurn;
    public string? RefusalCategory { get; init; }
    public string? RefusalExplanation { get; init; }
    public TokenUsage Usage { get; init; } = TokenUsage.Zero;
}

public static class StopReasons
{
    public const string EndTurn = "end_turn";
    public const string MaxTokens = "max_tokens";
    public const string Refusal = "refusal";
}

public sealed record GlossaryRequest(string SystemPrompt, string UserMessage);

public sealed record GlossaryResponse(Glossary? Glossary, string StopReason, TokenUsage Usage);

public sealed class GlossaryTerm
{
    [JsonPropertyName("en")] public string En { get; set; } = "";
    [JsonPropertyName("es")] public string Es { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

public sealed class AddressRule
{
    [JsonPropertyName("a")] public string A { get; set; } = "";
    [JsonPropertyName("b")] public string B { get; set; } = "";
    /// <summary>"tú" o "usted".</summary>
    [JsonPropertyName("form")] public string Form { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

/// <summary>Glosario del video: términos y formas de tratamiento entre personajes.</summary>
public sealed class Glossary
{
    [JsonPropertyName("terms")] public List<GlossaryTerm> Terms { get; set; } = [];
    [JsonPropertyName("address")] public List<AddressRule> Address { get; set; } = [];
}

/// <summary>Progreso de una traducción.</summary>
public sealed record TranslationProgress(
    string Stage,
    int ChunkNumber,
    int ChunkCount,
    int BlocksDone,
    int BlocksTotal,
    TokenUsage Usage,
    decimal EstimatedCostUsd);

/// <summary>Backends de traducción: Claude Code (suscripción) o la API de Anthropic (clave de API).</summary>
public static class TranslationBackends
{
    public const string ClaudeCode = "claude-code";
    public const string Api = "api";
}

public sealed record TranslationOptions
{
    /// <summary><see cref="TranslationBackends.ClaudeCode"/> o <see cref="TranslationBackends.Api"/>. Se guarda en job.json.</summary>
    public string Backend { get; init; } = TranslationBackends.ClaudeCode;
    public string Model { get; init; } = ModelPricing.DefaultModel;
    /// <summary>"low", "medium" o "high" (Claude Code admite además "xhigh" y "max").</summary>
    public string Effort { get; init; } = "medium";
    public int ChunkSize { get; init; } = 150;
    public int ContextSize { get; init; } = 10;
    /// <summary>Reintentos para ids faltantes o vacíos.</summary>
    public int MaxMissingRetries { get; init; } = 2;
    /// <summary>Reintentos por JSON inválido.</summary>
    public int MaxInvalidJsonRetries { get; init; } = 2;
    public bool Restart { get; init; }
    public bool Force { get; init; }
    public bool NoPlainCopy { get; init; }
    public bool RegenerateGlossary { get; init; }
    /// <summary>No escribir el .es.srt: dejar un borrador para revisarlo lado a lado (ventana «Revisar traducción»).</summary>
    public bool ReviewBeforeSave { get; init; }
}

public sealed record FailedBlock(int Id, string Reason);

public enum TranslationStatus
{
    Completed,
    /// <summary>El .es.srt ya existía y no se pidió --force.</summary>
    SkippedExists,
    Failed,
    /// <summary>Traducida y validada, pero guardada como borrador para revisarla antes de crear el .es.srt.</summary>
    PendingReview,
}

public enum PlainCopyStatus
{
    NotAttempted,
    Created,
    /// <summary>Ya había un .srt sin idioma: no se tocó.</summary>
    SkippedExists,
    Disabled,
    /// <summary>No se pudo escribir (archivo bloqueado o sin permisos); el .es.srt sí se guardó.</summary>
    Failed,
}

public sealed record TranslationResult
{
    public required TranslationStatus Status { get; init; }
    public required string OutputPath { get; init; }
    public string? PlainCopyPath { get; init; }
    public PlainCopyStatus PlainCopy { get; init; } = PlainCopyStatus.NotAttempted;
    public int BlockCount { get; init; }
    public IReadOnlyList<FailedBlock> FailedBlocks { get; init; } = [];
    public ValidationReport? Validation { get; init; }
    public TokenUsage Usage { get; init; } = TokenUsage.Zero;
    public decimal EstimatedCostUsd { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string Message { get; init; } = "";
    public string? LogPath { get; init; }
}

/// <summary>Error de la API de traducción con mensaje para el usuario (sin la clave).</summary>
public sealed class TranslationClientException : Exception
{
    public bool IsAuthentication { get; }

    /// <summary>Límite de uso de la suscripción alcanzado: el trabajo se detiene y se reanuda más tarde.</summary>
    public bool IsUsageLimit { get; }

    public TranslationClientException(string message, Exception? inner = null, bool isAuthentication = false, bool isUsageLimit = false)
        : base(message, inner)
    {
        IsAuthentication = isAuthentication;
        IsUsageLimit = isUsageLimit;
    }
}
