namespace SubtitleTool.Core.Translation;

/// <summary>Cliente del modelo de traducción. La implementación real usa la API de Claude; en pruebas, un cliente falso.</summary>
public interface ITranslationClient
{
    Task<ChunkResponse> TranslateChunkAsync(ChunkRequest request, CancellationToken cancellationToken = default);

    Task<GlossaryResponse> BuildGlossaryAsync(GlossaryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Tokens de entrada de un mensaje (prompt de sistema opcional + mensaje de usuario).</summary>
    Task<long> CountTokensAsync(string? systemPrompt, string userMessage, CancellationToken cancellationToken = default);
}
