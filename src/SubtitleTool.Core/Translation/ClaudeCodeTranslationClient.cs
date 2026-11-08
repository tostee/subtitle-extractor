using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SubtitleTool.Core.Processes;

namespace SubtitleTool.Core.Translation;

/// <summary>Salida ya interpretada de una ejecución de <c>claude -p --output-format json</c>.</summary>
public sealed record ClaudeCodeOutput
{
    public int ExitCode { get; init; }
    /// <summary>false si stdout no contenía el objeto JSON de resultado.</summary>
    public bool HasJson { get; init; }
    public bool IsError { get; init; }
    public string Subtype { get; init; } = "";
    public string StopReason { get; init; } = "";
    /// <summary>Campo "result": texto final (o mensaje de error si is_error).</summary>
    public string ResultText { get; init; } = "";
    /// <summary>Campo "structured_output" serializado (solo con --json-schema); null si no vino.</summary>
    public string? StructuredJson { get; init; }
    public int? ApiErrorStatus { get; init; }
    public long DurationMs { get; init; }
    public TokenUsage Usage { get; init; } = TokenUsage.Zero;
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";
}

/// <summary>
/// Traduce con Claude Code instalado localmente (<c>claude -p</c>), usando la sesión/suscripción del usuario en vez de la API.
/// El mensaje de usuario va por stdin; el prompt de sistema por argumento (o archivo si es largo); la salida se valida con --json-schema.
/// Sin herramientas, sin MCP, sin comandos, sin ajustes de usuario/proyecto y sin guardar sesión; se ejecuta en una carpeta temporal vacía.
/// </summary>
public sealed class ClaudeCodeTranslationClient : ITranslationClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>A partir de esta longitud el prompt de sistema se pasa con --system-prompt-file (la línea de comandos de Windows admite ~32K caracteres).</summary>
    public const int SystemPromptFileThreshold = 8000;

    /// <summary>Variables que harían que Claude Code facture por la API en lugar de la suscripción.</summary>
    public static readonly IReadOnlyList<string> RemovedEnvironmentVariables = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"];

    private static readonly string ChunkSchema = Minify(TranslationPrompts.ChunkSchemaJson);
    private static readonly string GlossarySchema = Minify(TranslationPrompts.GlossarySchemaJson);

    private static readonly Regex UsageLimitPattern = new(
        @"hit your (\w+ )?limit|usage limit|(5-hour|weekly|session|spend|opus|sonnet|fable) limit|out of extra usage|requires usage credits|limit reached.{0,60}reset|resets? (at|in) ",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AuthPattern = new(
        @"not logged in|/login|invalid api key|oauth token|authentication_error|please log in|credentials? (have |has )?expired",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IProcessRunner _runner;
    private readonly string _executable;
    private readonly string _model;
    private readonly string _effort;
    private readonly TimeSpan _timeout;
    private readonly string _workingDirectory;

    public ClaudeCodeTranslationClient(string model, string effort, TimeSpan? timeout = null, IProcessRunner? runner = null,
        string? executable = null, string? workingDirectory = null)
    {
        ModelPricing.For(model); // valida el modelo
        if (effort is not ("low" or "medium" or "high" or "xhigh" or "max"))
            throw new ArgumentException($"Esfuerzo no válido: '{effort}'. Usa low, medium, high, xhigh o max.", nameof(effort));
        _model = model;
        _effort = effort;
        _timeout = timeout ?? DefaultTimeout;
        _runner = runner ?? new ProcessRunner();
        _executable = executable ?? ResolveExecutable();
        _workingDirectory = workingDirectory ?? Path.Combine(Path.GetTempPath(), "SubtitleTool-claude-cwd");
    }

    public string Executable => _executable;

    /// <summary>Busca claude.exe en el PATH y en la instalación nativa por defecto; si no, "claude" (lo resolverá el sistema).</summary>
    public static string ResolveExecutable()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), OperatingSystem.IsWindows() ? "claude.exe" : "claude");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Entrada del PATH con caracteres no válidos: se ignora.
            }
        }
        return "claude";
    }

    /// <summary>Argumentos de una llamada (sin el prompt de usuario, que va por stdin).</summary>
    public static List<string> BuildArguments(string model, string effort, string? systemPrompt, string? systemPromptFile, string? schemaJson)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "json",
            "--model", model,
            "--effort", effort,
            "--tools", "",
            "--no-session-persistence",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--setting-sources", "",
        };
        if (systemPromptFile is not null)
            args.AddRange(["--system-prompt-file", systemPromptFile]);
        else if (systemPrompt is not null)
            args.AddRange(["--system-prompt", systemPrompt]);
        if (schemaJson is not null)
            args.AddRange(["--json-schema", schemaJson]);
        return args;
    }

    public async Task<ChunkResponse> TranslateChunkAsync(ChunkRequest request, CancellationToken cancellationToken = default)
    {
        var o = await RunAsync(request.SystemPrompt, request.UserMessage, ChunkSchema, _model, cancellationToken).ConfigureAwait(false);
        return ToChunkResponse(o);
    }

    public async Task<GlossaryResponse> BuildGlossaryAsync(GlossaryRequest request, CancellationToken cancellationToken = default)
    {
        var o = await RunAsync(request.SystemPrompt, request.UserMessage, GlossarySchema, _model, cancellationToken).ConfigureAwait(false);
        var c = Classify(o);
        if (c.StopReason != StopReasons.EndTurn)
            return new GlossaryResponse(null, c.StopReason, o.Usage);
        return new GlossaryResponse(TranslationPrompts.ParseGlossary(c.Json), StopReasons.EndTurn, o.Usage);
    }

    /// <summary>Sin API de conteo: estimación local (caracteres / 4).</summary>
    public Task<long> CountTokensAsync(string? systemPrompt, string userMessage, CancellationToken cancellationToken = default) =>
        Task.FromResult(EstimateTokens((systemPrompt?.Length ?? 0) + userMessage.Length));

    public static long EstimateTokens(int characters) => (characters + 3) / 4;

    /// <summary>
    /// Ejecuta una llamada. Lanza <see cref="TranslationClientException"/> si Claude Code no está instalado, no hay sesión,
    /// se alcanzó el límite de uso o se superó el tiempo máximo. El resto de errores se devuelven en la salida (reintentables).
    /// </summary>
    public async Task<ClaudeCodeOutput> RunAsync(string? systemPrompt, string userMessage, string? schemaJson, string? model = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_workingDirectory);
        string? promptFile = null;
        try
        {
            if (systemPrompt is not null && systemPrompt.Length > SystemPromptFileThreshold)
            {
                promptFile = Path.Combine(Path.GetTempPath(), $"subtool-system-{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(promptFile, systemPrompt, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            var args = BuildArguments(model ?? _model, _effort, promptFile is null ? systemPrompt : null, promptFile, schemaJson);
            var options = new ProcessRunOptions
            {
                StandardInput = userMessage,
                WorkingDirectory = _workingDirectory,
                RemoveEnvironmentVariables = RemovedEnvironmentVariables,
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);
            ProcessResult result;
            try
            {
                result = await _runner.RunAsync(_executable, args, options, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TranslationClientException(
                    $"Claude Code no respondió en {_timeout.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} min (proceso detenido). " +
                    "El progreso queda guardado: relanza el comando (puedes subir el límite con --timeout o bajar --chunk).");
            }
            catch (Win32Exception ex)
            {
                throw new TranslationClientException(
                    $"No se pudo ejecutar Claude Code ('{_executable}'): no está instalado o no está en el PATH. " +
                    "Instálalo (https://claude.com/claude-code) e inicia sesión ejecutando 'claude' y luego /login. Comprueba con 'subtool doctor'.", ex);
            }

            var output = Parse(result);
            ThrowIfFatal(output);
            return output;
        }
        finally
        {
            if (promptFile is not null)
            {
                try { File.Delete(promptFile); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Interpreta stdout: un único objeto JSON (si hay ruido, se toma la última línea que sea un objeto JSON).</summary>
    public static ClaudeCodeOutput Parse(ProcessResult r)
    {
        var baseOutput = new ClaudeCodeOutput { ExitCode = r.ExitCode, StandardOutput = r.StandardOutput, StandardError = r.StandardError };
        var root = TryParseObject(r.StandardOutput.Trim());
        if (root is null)
        {
            foreach (var line in r.StandardOutput.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')).Reverse())
            {
                root = TryParseObject(line);
                if (root is not null)
                    break;
            }
        }
        if (root is not { } e)
            return baseOutput;

        using var _ = root;
        var el = e.RootElement;
        return baseOutput with
        {
            HasJson = true,
            IsError = el.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True,
            Subtype = Str(el, "subtype"),
            StopReason = Str(el, "stop_reason"),
            ResultText = Str(el, "result"),
            StructuredJson = el.TryGetProperty("structured_output", out var so) && so.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? so.GetRawText()
                : null,
            ApiErrorStatus = el.TryGetProperty("api_error_status", out var st) && st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out var s) ? s : null,
            DurationMs = Num(el, "duration_ms"),
            Usage = UsageOf(el),
        };
    }

    private static TokenUsage UsageOf(JsonElement el)
    {
        var cost = el.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var d) ? d : 0m;
        if (!el.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
            return TokenUsage.Zero with { ReportedCostUsd = cost };
        return new TokenUsage(Num(u, "input_tokens"), Num(u, "output_tokens"), Num(u, "cache_read_input_tokens"), Num(u, "cache_creation_input_tokens"))
        {
            ReportedCostUsd = cost,
        };
    }

    /// <summary>Errores que no se arreglan reintentando: sesión no iniciada o límite de uso alcanzado.</summary>
    private static void ThrowIfFatal(ClaudeCodeOutput o)
    {
        if (o.HasJson && !o.IsError && o.ExitCode == 0)
            return;
        var text = ErrorText(o);
        if (o.ApiErrorStatus == 429 || UsageLimitPattern.IsMatch(text))
        {
            throw new TranslationClientException(
                $"Límite de uso de la suscripción de Claude alcanzado: \"{Shorten(text)}\". " +
                "El trabajo se detiene y el progreso queda guardado: relanza el mismo comando cuando se restablezca el límite.",
                isUsageLimit: true);
        }
        if (o.ApiErrorStatus is 401 or 403 || AuthPattern.IsMatch(text))
        {
            throw new TranslationClientException(
                $"Claude Code no tiene sesión iniciada o la sesión caducó: \"{Shorten(text)}\". " +
                "Ejecuta 'claude' en una terminal y usa /login con tu cuenta (suscripción); luego relanza el comando.",
                isAuthentication: true);
        }
    }

    private static string ErrorText(ClaudeCodeOutput o)
    {
        var parts = new[] { o.ResultText, o.HasJson ? "" : o.StandardOutput, o.StandardError }
            .Select(p => p.Trim()).Where(p => p.Length > 0);
        return string.Join(" | ", parts);
    }

    /// <summary>Resultado clasificado: StopReason del orquestador + JSON (si EndTurn).</summary>
    private sealed record Classified(string StopReason, string? Json, string? Explanation);

    private static Classified Classify(ClaudeCodeOutput o)
    {
        if (!o.HasJson || o.IsError || o.ExitCode != 0)
        {
            var detail = $"error de Claude Code (código {o.ExitCode}{(o.ApiErrorStatus is { } s ? $", HTTP {s}" : "")}" +
                         $"{(o.Subtype.Length > 0 ? $", {o.Subtype}" : "")}): {Shorten(ErrorText(o))}";
            return new Classified(detail, null, null);
        }
        if (o.StopReason == StopReasons.MaxTokens)
            return new Classified(StopReasons.MaxTokens, null, null);
        if (o.StopReason == StopReasons.Refusal)
            return new Classified(StopReasons.Refusal, null, Shorten(o.ResultText));
        if (o.StructuredJson is not null)
            return new Classified(StopReasons.EndTurn, o.StructuredJson, null);

        // Sin salida estructurada: el texto puede ser JSON (quizá entre ```), JSON roto o una negativa en prosa.
        var text = StripFences(o.ResultText);
        if (text.Length == 0 || text.StartsWith('{') || text.StartsWith('['))
            return new Classified(StopReasons.EndTurn, text, null);
        return new Classified(StopReasons.Refusal, null, Shorten(o.ResultText));
    }

    private static ChunkResponse ToChunkResponse(ClaudeCodeOutput o)
    {
        var c = Classify(o);
        return new ChunkResponse
        {
            Items = c.StopReason == StopReasons.EndTurn ? TranslationPrompts.ParseItems(c.Json) : null,
            StopReason = c.StopReason,
            RefusalCategory = c.StopReason == StopReasons.Refusal ? "claude-code" : null,
            RefusalExplanation = c.Explanation,
            Usage = o.Usage,
        };
    }

    private static string StripFences(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal))
            return t;
        var firstNewLine = t.IndexOf('\n');
        var end = t.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewLine >= 0 && end > firstNewLine ? t[(firstNewLine + 1)..end].Trim() : t;
    }

    private static string Shorten(string text)
    {
        var t = text.ReplaceLineEndings(" ").Trim();
        return t.Length <= 300 ? t : t[..300] + "…";
    }

    private static JsonDocument? TryParseObject(string text)
    {
        if (!text.StartsWith('{'))
            return null;
        try
        {
            var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out _))
                return doc;
            doc.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string Minify(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement, TranslationPrompts.JsonOptions);
    }
}
