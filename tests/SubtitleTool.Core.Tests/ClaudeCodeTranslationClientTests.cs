using System.ComponentModel;
using System.Text;
using System.Text.Json;
using SubtitleTool.Core.Processes;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Tests;

/// <summary>Ejecución de procesos falsa: registra la llamada y devuelve una salida grabada (sin claude real).</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    public sealed record Call(string FileName, List<string> Arguments, ProcessRunOptions Options, string? SystemPromptFileContent, CancellationToken Token);

    public List<Call> Calls { get; } = [];
    public Func<Call, int, Task<ProcessResult>> Handler { get; set; } = (_, _) => Task.FromResult(new ProcessResult(0, "", ""));

    public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken = default) =>
        RunAsync(fileName, arguments, new ProcessRunOptions(), cancellationToken);

    public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, ProcessRunOptions options, CancellationToken cancellationToken = default)
    {
        var args = arguments.ToList();
        var fileIndex = args.IndexOf("--system-prompt-file");
        var fileContent = fileIndex >= 0 && File.Exists(args[fileIndex + 1]) ? File.ReadAllText(args[fileIndex + 1]) : null;
        var call = new Call(fileName, args, options, fileContent, cancellationToken);
        Calls.Add(call);
        return Handler(call, Calls.Count - 1);
    }
}

public sealed class ClaudeCodeTranslationClientTests : IDisposable
{
    private readonly string _dir;
    private readonly FakeProcessRunner _runner = new();

    public ClaudeCodeTranslationClientTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "subtool-cc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ClaudeCodeTranslationClient Client(TimeSpan? timeout = null, string model = "claude-opus-5-5", string effort = "medium") =>
        new(model, effort, timeout, _runner, executable: "claude-fake.exe", workingDirectory: Path.Combine(_dir, "cwd"));

    /// <summary>Salida con la forma real de 'claude -p --output-format json' (campos relevantes).</summary>
    public static string Output(object? structured = null, string result = "", bool isError = false, string stopReason = "tool_use",
        int? apiErrorStatus = null, string subtype = "success")
    {
        var d = new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["subtype"] = subtype,
            ["is_error"] = isError,
            ["duration_ms"] = 3072,
            ["num_turns"] = 2,
            ["stop_reason"] = stopReason,
            ["api_error_status"] = apiErrorStatus,
            ["result"] = result,
            ["total_cost_usd"] = 0.0125,
            ["usage"] = new Dictionary<string, object>
            {
                ["input_tokens"] = 12,
                ["cache_creation_input_tokens"] = 2888,
                ["cache_read_input_tokens"] = 400,
                ["output_tokens"] = 237,
            },
        };
        if (structured is not null)
            d["structured_output"] = structured;
        return JsonSerializer.Serialize(d);
    }

    /// <summary>Traduce lo que llega por stdin (último renglón = JSON de bloques) anteponiendo "ES: ".</summary>
    public static ProcessResult EchoTranslation(FakeProcessRunner.Call call)
    {
        var stdin = call.Options.StandardInput!;
        var items = JsonSerializer.Deserialize<List<TranslationItem>>(stdin[(stdin.LastIndexOf('\n') + 1)..])!;
        var structured = new { items = items.Select(i => new { id = i.Id, text = "ES: " + i.Text }) };
        return new ProcessResult(0, Output(structured), "");
    }

    private static ChunkRequest Request(params string[] texts) => new()
    {
        ChunkNumber = 1,
        SystemPrompt = TranslationPrompts.SystemPrompt,
        UserMessage = TranslationPrompts.BuildChunkUserMessage(new Glossary(), [],
            texts.Select((t, i) => new TranslationItem(i + 1, t)).ToList()),
        Items = texts.Select((t, i) => new TranslationItem(i + 1, t)).ToList(),
    };

    // ----------------------------------------------------------------- éxito y argumentos

    [Fact]
    public async Task Success_ParsesStructuredOutput_UsageAndCost_AndBuildsArguments()
    {
        _runner.Handler = (c, _) => Task.FromResult(EchoTranslation(c));
        var request = Request("Hello.", "Bye.");

        var r = await Client(effort: "high").TranslateChunkAsync(request);

        Assert.Equal(StopReasons.EndTurn, r.StopReason);
        Assert.Equal([new TranslationItem(1, "ES: Hello."), new TranslationItem(2, "ES: Bye.")], r.Items);
        Assert.Equal(new TokenUsage(12, 237, 400, 2888) { ReportedCostUsd = 0.0125m }, r.Usage);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal("claude-fake.exe", call.FileName);
        var a = call.Arguments;
        Assert.Equal("-p", a[0]);
        Assert.Equal("json", a[a.IndexOf("--output-format") + 1]);
        Assert.Equal("", a[a.IndexOf("--tools") + 1]);
        Assert.Contains("--no-session-persistence", a);
        Assert.Contains("--strict-mcp-config", a);
        Assert.Contains("--disable-slash-commands", a);
        Assert.Equal("", a[a.IndexOf("--setting-sources") + 1]);
        Assert.DoesNotContain("--bare", a);
        Assert.Equal("claude-opus-5-5", a[a.IndexOf("--model") + 1]);
        Assert.Equal("high", a[a.IndexOf("--effort") + 1]);
        Assert.Equal(TranslationPrompts.SystemPrompt, a[a.IndexOf("--system-prompt") + 1]);
        using (var schema = JsonDocument.Parse(a[a.IndexOf("--json-schema") + 1]))
            Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("items", out _));
        // El mensaje de usuario va por stdin, nunca como argumento.
        Assert.Equal(request.UserMessage, call.Options.StandardInput);
        Assert.DoesNotContain(a, x => x.Contains("Hello.", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(_dir, "cwd"), call.Options.WorkingDirectory);
        Assert.True(Directory.Exists(Path.Combine(_dir, "cwd")));
        Assert.Contains("ANTHROPIC_API_KEY", call.Options.RemoveEnvironmentVariables);
    }

    [Fact]
    public async Task LongSystemPrompt_GoesThroughTemporaryFile_ThatIsDeleted()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(0, Output(new { items = new[] { new { id = 1, text = "Hola." } } }), ""));
        var longPrompt = new string('x', ClaudeCodeTranslationClient.SystemPromptFileThreshold + 1);
        var request = Request("Hello.") with { SystemPrompt = longPrompt };

        await Client().TranslateChunkAsync(request);

        var call = Assert.Single(_runner.Calls);
        Assert.DoesNotContain("--system-prompt", call.Arguments);
        var file = call.Arguments[call.Arguments.IndexOf("--system-prompt-file") + 1];
        Assert.Equal(longPrompt, call.SystemPromptFileContent);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Glossary_ParsesStructuredOutput_WithGlossarySchema()
    {
        var g = new { terms = new[] { new { en = "Odysseus", es = "Odiseo", note = "rey" } }, address = Array.Empty<object>() };
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(0, Output(g), ""));

        var r = await Client().BuildGlossaryAsync(new GlossaryRequest(TranslationPrompts.GlossarySystemPrompt, "Odysseus"));

        Assert.Equal(StopReasons.EndTurn, r.StopReason);
        Assert.Equal("Odiseo", Assert.Single(r.Glossary!.Terms).Es);
        var a = _runner.Calls[0].Arguments;
        Assert.Contains("\"address\"", a[a.IndexOf("--json-schema") + 1]);
    }

    [Fact]
    public async Task ResultTextJson_WithoutStructuredOutput_IsAccepted()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(0,
            Output(result: "```json\n{\"items\":[{\"id\":1,\"text\":\"Hola.\"}]}\n```", stopReason: "end_turn"), ""));

        var r = await Client().TranslateChunkAsync(Request("Hello."));

        Assert.Equal([new TranslationItem(1, "Hola.")], r.Items);
    }

    // ----------------------------------------------------------------- errores reintentables

    [Fact]
    public async Task IsError_Generic_ReturnsRetryableResponse()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(1,
            Output(result: "API Error: 529 Overloaded", isError: true, stopReason: "stop_sequence", apiErrorStatus: 529), ""));

        var r = await Client().TranslateChunkAsync(Request("Hello."));

        Assert.Null(r.Items);
        Assert.StartsWith("error", r.StopReason);
        Assert.Contains("529", r.StopReason);
    }

    [Fact]
    public async Task InvalidJsonOutput_ReturnsNullItems()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(0, "esto no es JSON {", ""));

        var r = await Client().TranslateChunkAsync(Request("Hello."));

        Assert.Null(r.Items);
        Assert.StartsWith("error", r.StopReason);
    }

    [Fact]
    public async Task BrokenJsonInResultText_ReturnsNullItems_NotRefusal()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(0, Output(result: "{\"items\":[{\"id\":1,", stopReason: "end_turn"), ""));

        var r = await Client().TranslateChunkAsync(Request("Hello."));

        Assert.Equal(StopReasons.EndTurn, r.StopReason);
        Assert.Null(r.Items);
    }

    [Fact]
    public async Task NonZeroExitCode_WithoutJson_ReturnsRetryableResponseWithStderr()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(3, "", "Error: algo se rompió"));

        var r = await Client().TranslateChunkAsync(Request("Hello."));

        Assert.Null(r.Items);
        Assert.Contains("código 3", r.StopReason);
        Assert.Contains("algo se rompió", r.StopReason);
    }

    [Fact]
    public async Task RefusalText_WithoutJson_IsTreatedAsRefusal()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(0,
            Output(result: "I'm sorry, but I can't help translate this content.", stopReason: "end_turn"), ""));

        var r = await Client().TranslateChunkAsync(Request("Hello."));

        Assert.Equal(StopReasons.Refusal, r.StopReason);
        Assert.Null(r.Items);
        Assert.Contains("can't help", r.RefusalExplanation);
    }

    [Fact]
    public async Task RefusalStopReason_IsTreatedAsRefusal()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(0, Output(result: "", stopReason: "refusal"), ""));

        var r = await Client().TranslateChunkAsync(Request("Hello."));

        Assert.Equal(StopReasons.Refusal, r.StopReason);
    }

    [Fact]
    public async Task MaxTokens_IsMapped()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(0, Output(result: "{\"items\":[", stopReason: "max_tokens"), ""));

        var r = await Client().TranslateChunkAsync(Request("Hello."));

        Assert.Equal(StopReasons.MaxTokens, r.StopReason);
    }

    // ----------------------------------------------------------------- errores que detienen el trabajo

    [Theory]
    [InlineData("You've hit your limit · resets 3pm (America/Mexico_City)", null)]
    [InlineData("Claude AI usage limit reached|1762560000", null)]
    [InlineData("API Error: Rate limited", 429)]
    public async Task UsageLimit_ThrowsUsageLimitException(string message, int? status)
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(1,
            Output(result: message, isError: true, stopReason: "stop_sequence", apiErrorStatus: status), ""));

        var ex = await Assert.ThrowsAsync<TranslationClientException>(() => Client().TranslateChunkAsync(Request("Hello.")));

        Assert.True(ex.IsUsageLimit);
        Assert.Contains("Límite de uso", ex.Message);
    }

    [Fact]
    public async Task NotLoggedIn_ThrowsAuthenticationException()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(1,
            Output(result: "Not logged in · Please run /login", isError: true, stopReason: "stop_sequence"), ""));

        var ex = await Assert.ThrowsAsync<TranslationClientException>(() => Client().TranslateChunkAsync(Request("Hello.")));

        Assert.True(ex.IsAuthentication);
        Assert.Contains("/login", ex.Message);
    }

    [Fact]
    public async Task ClaudeNotInstalled_ThrowsClearMessage()
    {
        _runner.Handler = (_, _) => throw new Win32Exception(2, "The system cannot find the file specified.");

        var ex = await Assert.ThrowsAsync<TranslationClientException>(() => Client().TranslateChunkAsync(Request("Hello.")));

        Assert.Contains("no está instalado", ex.Message);
    }

    [Fact]
    public async Task Timeout_KillsAndThrowsClientException()
    {
        _runner.Handler = (c, _) => DelayUntilCancelled(c);

        var ex = await Assert.ThrowsAsync<TranslationClientException>(() => Client(TimeSpan.FromMilliseconds(100)).TranslateChunkAsync(Request("Hello.")));

        Assert.Contains("no respondió", ex.Message);
    }

    [Fact]
    public async Task UserCancellation_PropagatesOperationCanceled()
    {
        _runner.Handler = (c, _) => DelayUntilCancelled(c);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client().TranslateChunkAsync(Request("Hello."), cts.Token));
    }

    /// <summary>Simula un proceso colgado: solo termina cuando se cancela (el runner real lo mataría).</summary>
    private static async Task<ProcessResult> DelayUntilCancelled(FakeProcessRunner.Call call)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), call.Token);
        return new ProcessResult(0, "", "");
    }

    // ----------------------------------------------------------------- integración con el orquestador

    [Fact]
    public async Task UsageLimit_StopsJob_KeepsResumableState_WithoutFailedBlocks()
    {
        var en = Path.Combine(_dir, "Movie.en.srt");
        File.WriteAllText(en, SubtitleTranslatorTests.MakeSrt(20), new UTF8Encoding(false));
        var jobs = Path.Combine(_dir, "_jobs");
        var options = new TranslationOptions { ChunkSize = 10 };
        var glossary = new { terms = Array.Empty<object>(), address = Array.Empty<object>() };

        // 1.ª ejecución: glosario OK, tramo 1 OK, tramo 2 → límite de uso.
        _runner.Handler = (c, i) => Task.FromResult(i switch
        {
            0 => new ProcessResult(0, Output(glossary), ""),
            1 => EchoTranslation(c),
            _ => new ProcessResult(1, Output(result: "You've hit your limit · resets 3pm", isError: true, stopReason: "stop_sequence"), ""),
        });
        var ex = await Assert.ThrowsAsync<TranslationClientException>(() =>
            new SubtitleTranslator(Client(), options, jobs).TranslateFileAsync(en));
        Assert.True(ex.IsUsageLimit);
        Assert.False(File.Exists(Path.Combine(_dir, "Movie.es.srt")));
        var store = new JobStore(jobs, en);
        Assert.True(store.Exists);
        Assert.Equal(TranslationBackends.ClaudeCode, store.LoadJob()!.Backend);
        Assert.True(store.LoadChunk(1)!.Complete);
        var chunk2 = store.LoadChunk(2)!;
        Assert.False(chunk2.Complete);
        Assert.Empty(chunk2.Items);

        // 2.ª ejecución (límite restablecido): solo se traduce el tramo 2.
        _runner.Calls.Clear();
        _runner.Handler = (c, _) => Task.FromResult(EchoTranslation(c));
        var r = await new SubtitleTranslator(Client(), options, jobs).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Completed, r.Status);
        Assert.Empty(r.FailedBlocks);
        var call = Assert.Single(_runner.Calls);
        Assert.Contains("\"id\":11", call.Options.StandardInput);
        Assert.Equal(0.0125m, r.Usage.ReportedCostUsd);
        Assert.Equal(0.0125m, r.EstimatedCostUsd);
        Assert.False(store.Exists);
    }

    [Fact]
    public async Task BackendChange_DiscardsSavedStateWithWarning()
    {
        var en = Path.Combine(_dir, "Movie.en.srt");
        File.WriteAllText(en, SubtitleTranslatorTests.MakeSrt(5), new UTF8Encoding(false));
        var jobs = Path.Combine(_dir, "_jobs");
        var store = new JobStore(jobs, en);
        store.SaveJob(new JobInfo { Source = en, Backend = TranslationBackends.Api, Model = ModelPricing.DefaultModel, ChunkSize = 150, PromptVersion = TranslationPrompts.PromptVersion });
        store.SaveChunk(1, new ChunkState { Items = [new TranslationItem(1, "VIEJO")] });

        var r = await new SubtitleTranslator(new FakeTranslationClient(), new TranslationOptions { Backend = TranslationBackends.ClaudeCode }, jobs)
            .TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Completed, r.Status);
        Assert.Contains(r.Warnings, w => w.Contains("backend api", StringComparison.Ordinal));
        Assert.DoesNotContain("VIEJO", File.ReadAllText(r.OutputPath));
    }

    [Fact]
    public async Task OldJobWithoutBackend_IsTreatedAsApi()
    {
        var en = Path.Combine(_dir, "Movie.en.srt");
        File.WriteAllText(en, SubtitleTranslatorTests.MakeSrt(5), new UTF8Encoding(false));
        var jobs = Path.Combine(_dir, "_jobs");
        var store = new JobStore(jobs, en);
        store.SaveJob(new JobInfo { Source = en, Model = ModelPricing.DefaultModel, ChunkSize = 150, PromptVersion = TranslationPrompts.PromptVersion });
        store.SaveChunk(1, new ChunkState { Items = [new TranslationItem(1, "GUARDADO")] });
        var fake = new FakeTranslationClient();

        var r = await new SubtitleTranslator(fake, new TranslationOptions { Backend = TranslationBackends.Api }, jobs).TranslateFileAsync(en);

        Assert.Empty(r.Warnings);
        Assert.Contains("GUARDADO", File.ReadAllText(r.OutputPath));
    }

    [Fact]
    public async Task CountTokens_IsLocalEstimate_WithoutCallingClaude()
    {
        var n = await Client().CountTokensAsync("abcd", "efghijkl");

        Assert.Equal(3, n);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public void Parse_TakesLastJsonLine_WhenThereIsNoise()
    {
        var o = ClaudeCodeTranslationClient.Parse(new ProcessResult(0, "aviso\n" + Output(result: "OK", stopReason: "end_turn") + "\n", ""));

        Assert.True(o.HasJson);
        Assert.Equal("OK", o.ResultText);
        Assert.Equal(3072, o.DurationMs);
    }
}
