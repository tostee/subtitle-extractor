using System.Text.Json;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Workflow;

/// <summary>Crea el cliente de traducción (Claude Code, API o el falso de pruebas) a partir de los ajustes.</summary>
public static class TranslationClientFactory
{
    /// <summary>Con <c>SUBTOOL_FAKE_TRANSLATOR=1</c> se usa un traductor falso determinista (sin red, sin clave).</summary>
    public const string FakeTranslatorVariable = "SUBTOOL_FAKE_TRANSLATOR";

    public const string MissingApiKeyMessage =
        "Falta la clave de API de Anthropic. Configúrala (se guarda cifrada) o usa tu suscripción de Claude (backend claude-code).";

    public static bool IsFakeEnabled => Environment.GetEnvironmentVariable(FakeTranslatorVariable) == "1";

    /// <summary>Devuelve null si el backend es <c>api</c> y no hay clave.</summary>
    public static ITranslationClient? Create(string backend, string model, string effort, TimeSpan timeout, ApiKeyStore? keyStore = null)
    {
        if (IsFakeEnabled)
            return new FakePrefixTranslationClient();
        if (backend == TranslationBackends.ClaudeCode)
            return new ClaudeCodeTranslationClient(model, effort, timeout);
        var key = (keyStore ?? new ApiKeyStore()).Resolve();
        if (key.Source == ApiKeySource.None || key.Key is null)
            return null;
        return new ClaudeTranslationClient(key.Key, model, effort);
    }
}

/// <summary>Traductor falso determinista para pruebas manuales: antepone "ES: " a cada bloque. No usa la red.</summary>
public sealed class FakePrefixTranslationClient : ITranslationClient
{
    public Task<ChunkResponse> TranslateChunkAsync(ChunkRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChunkResponse
        {
            Items = request.Items.Select(i => new TranslationItem(i.Id, "ES: " + i.Text)).ToList(),
            StopReason = StopReasons.EndTurn,
            Usage = new TokenUsage(request.UserMessage.Length / 4, request.Items.Sum(i => i.Text.Length) / 4, request.SystemPrompt.Length / 4, 0),
        });

    public Task<GlossaryResponse> BuildGlossaryAsync(GlossaryRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GlossaryResponse(
            JsonSerializer.Deserialize<Glossary>("""{"terms":[{"en":"Odysseus","es":"Odiseo","note":"falso"}],"address":[]}""")!,
            StopReasons.EndTurn, new TokenUsage(request.UserMessage.Length / 4, 50, 0, 0)));

    public Task<long> CountTokensAsync(string? systemPrompt, string userMessage, CancellationToken cancellationToken = default) =>
        Task.FromResult((long)(((systemPrompt?.Length ?? 0) + userMessage.Length) / 4));
}
