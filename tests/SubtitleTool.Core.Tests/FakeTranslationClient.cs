using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Tests;

/// <summary>
/// Cliente falso sin red. Por defecto traduce anteponiendo "ES: ". <see cref="Handler"/> permite simular
/// ids omitidos, rechazos, max_tokens, JSON inválido o excepciones; si devuelve null se usa el comportamiento por defecto.
/// </summary>
public sealed class FakeTranslationClient : ITranslationClient
{
    public List<ChunkRequest> Requests { get; } = [];
    public List<GlossaryRequest> GlossaryRequests { get; } = [];
    public Func<ChunkRequest, int, ChunkResponse?>? Handler { get; set; }
    public Glossary GlossaryToReturn { get; set; } = new()
    {
        Terms = [new GlossaryTerm { En = "Odysseus", Es = "Odiseo", Note = "rey de Ítaca" }],
        Address = [new AddressRule { A = "Odysseus", B = "Telemachus", Form = "tú", Note = "padre e hijo" }],
    };

    public static string Translate(string text) => "ES: " + text;

    public static ChunkResponse Ok(IEnumerable<TranslationItem> items) => new()
    {
        Items = items.Select(i => new TranslationItem(i.Id, Translate(i.Text))).ToList(),
        StopReason = StopReasons.EndTurn,
        Usage = new TokenUsage(100, 50, 0, 0),
    };

    public Task<ChunkResponse> TranslateChunkAsync(ChunkRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        var custom = Handler?.Invoke(request, Requests.Count - 1);
        return Task.FromResult(custom ?? Ok(request.Items));
    }

    public Task<GlossaryResponse> BuildGlossaryAsync(GlossaryRequest request, CancellationToken cancellationToken = default)
    {
        GlossaryRequests.Add(request);
        return Task.FromResult(new GlossaryResponse(GlossaryToReturn, StopReasons.EndTurn, new TokenUsage(10, 10, 0, 0)));
    }

    public Task<long> CountTokensAsync(string? systemPrompt, string userMessage, CancellationToken cancellationToken = default) =>
        Task.FromResult((long)((systemPrompt?.Length ?? 0) + userMessage.Length) / 4);
}
