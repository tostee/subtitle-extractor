using System.Globalization;
using System.Text;
using System.Text.Json;
using SubtitleTool.Core.Library;
using SubtitleTool.Core.Media;
using SubtitleTool.Core.Mkv;
using SubtitleTool.Core.Processes;
using SubtitleTool.Core.Srt;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.Cli;

/// <summary>Comandos de traducción: translate, glossary, run, key y doctor.</summary>
internal static class TranslationCommands
{
    private static readonly HashSet<string> TranslateOptions = new(StringComparer.OrdinalIgnoreCase) { "backend", "model", "effort", "chunk", "timeout" };
    private static readonly HashSet<string> TranslateFlags = new(StringComparer.OrdinalIgnoreCase)
        { "dry-run", "restart", "force", "no-plain-copy", "regenerate-glossary", "review" };

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record Settings(TranslationOptions Options, bool DryRun, TimeSpan Timeout)
    {
        public bool IsClaudeCode => Options.Backend == TranslationBackends.ClaudeCode;
    }

    private static Settings ParseSettings(ParsedArgs a)
    {
        var backend = (a.Get("backend") ?? TranslationBackends.ClaudeCode).ToLowerInvariant();
        if (backend is not (TranslationBackends.ClaudeCode or TranslationBackends.Api))
            throw new UsageException("--backend debe ser claude-code o api.");
        var model = a.Get("model") ?? ModelPricing.DefaultModel;
        model = model.ToLowerInvariant() switch
        {
            "opus" => "claude-opus-5-5",
            "sonnet" => "claude-sonnet-5-5",
            "haiku" => "claude-haiku-5-5",
            _ => model,
        };
        if (!ModelPricing.Known.ContainsKey(model))
            throw new UsageException($"Modelo no soportado: '{model}'. Usa: {string.Join(", ", ModelPricing.Known.Keys)} (o opus, sonnet, haiku).");
        var effort = (a.Get("effort") ?? "medium").ToLowerInvariant();
        if (backend == TranslationBackends.Api && effort is not ("low" or "medium" or "high"))
            throw new UsageException("--effort debe ser low, medium o high.");
        if (effort is not ("low" or "medium" or "high" or "xhigh" or "max"))
            throw new UsageException("--effort debe ser low, medium, high, xhigh o max (xhigh/max solo con --backend claude-code).");
        var chunk = 150;
        if (a.Get("chunk") is { } c && (!int.TryParse(c, NumberStyles.None, Inv, out chunk) || chunk < 1))
            throw new UsageException("--chunk debe ser un número entero >= 1.");
        var timeout = ClaudeCodeTranslationClient.DefaultTimeout;
        if (a.Get("timeout") is { } t)
        {
            if (!int.TryParse(t, NumberStyles.None, Inv, out var minutes) || minutes < 1)
                throw new UsageException("--timeout debe ser un número entero de minutos >= 1.");
            timeout = TimeSpan.FromMinutes(minutes);
        }

        return new Settings(new TranslationOptions
        {
            Backend = backend,
            Model = model,
            Effort = effort,
            ChunkSize = chunk,
            Restart = a.Has("restart"),
            Force = a.Has("force"),
            NoPlainCopy = a.Has("no-plain-copy"),
            ReviewBeforeSave = a.Has("review"),
            RegenerateGlossary = a.Has("regenerate-glossary"),
        }, a.Has("dry-run"), timeout);
    }

    private static TranslationPaths RequireEnglishSrt(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"No existe el archivo: {path}", path);
        if (!TranslationPaths.TryFromEnglish(path, out var p))
            throw new UsageException($"El archivo debe terminar en .en.srt o .en.sdh.srt: {Path.GetFileName(path)}");
        return p!;
    }

    /// <summary>Crea el cliente (real o falso). Devuelve null e imprime el motivo si falta la clave (backend api).</summary>
    private static ITranslationClient? CreateClient(Settings s)
    {
        var o = s.Options;
        if (TranslationClientFactory.IsFakeEnabled)
            Console.WriteLine($"ℹ️  {TranslationClientFactory.FakeTranslatorVariable}=1: se usa el traductor FALSO de prueba (no llama a la API).");
        var client = TranslationClientFactory.Create(o.Backend, o.Model, o.Effort, s.Timeout);
        if (client is null)
        {
            Console.Error.WriteLine("❌ Falta la clave de API de Anthropic.");
            Console.Error.WriteLine("   Configúrala con 'subtool key set' (se guarda cifrada) o con la variable de entorno ANTHROPIC_API_KEY,");
            Console.Error.WriteLine("   o usa tu suscripción de Claude con --backend claude-code (por defecto).");
        }
        return client;
    }

    // ------------------------------------------------------------ translate

    public static async Task<int> TranslateAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var a = ParsedArgs.Parse(args, TranslateOptions, TranslateFlags);
        a.RequirePositionals(1, "subtool translate <archivo.en.srt> [--backend claude-code|api] [--model M] [--effort E] [--chunk N] [--timeout MIN] [--dry-run] [--restart] [--force] [--no-plain-copy] [--regenerate-glossary]");
        var paths = RequireEnglishSrt(a.Positionals[0]);
        var s = ParseSettings(a);

        var client = CreateClient(s);
        if (client is null)
            return ExitCodes.Usage;

        if (s.DryRun)
            return await EstimateAsync(client, s, paths, ct);

        var (status, _, _) = await TranslateOneAsync(client, s, paths, ct);
        return status == TranslationStatus.Failed ? ExitCodes.Failure : ExitCodes.Ok;
    }

    private static async Task<int> EstimateAsync(ITranslationClient client, Settings s, TranslationPaths paths, CancellationToken ct)
    {
        var translator = new SubtitleTranslator(client, s.Options);
        Console.WriteLine(s.IsClaudeCode
            ? $"Estimando tokens localmente (caracteres/4, sin llamar a Claude) de {Path.GetFileName(paths.EnglishPath)}…"
            : $"Contando tokens (sin traducir) de {Path.GetFileName(paths.EnglishPath)}…");
        var e = await translator.EstimateAsync(paths.EnglishPath, ct);
        PrintEstimate(e, s);
        return ExitCodes.Ok;
    }

    private static void PrintEstimate(CostEstimate e, Settings s)
    {
        if (s.IsClaudeCode)
        {
            var calls = e.ChunkCount + (e.GlossaryInputTokens > 0 ? 1 : 0);
            Console.WriteLine($"Estimación (Claude Code / suscripción, modelo {e.Model}): {e.ChunkCount} tramo(s), {calls} llamada(s) a 'claude'" +
                              (e.GlossaryInputTokens > 0 ? " (incluye la del glosario)" : " (glosario ya existe)"));
            Console.WriteLine($"   Entrada total:   ≈{e.TotalInputTokens:N0} tokens (estimación local: caracteres/4)");
            Console.WriteLine($"   Salida estimada: ≈{e.EstimatedOutputTokens:N0} tokens");
            Console.WriteLine("   Coste: incluido en la suscripción (consume parte de tu límite de uso).");
            return;
        }
        Console.WriteLine($"Estimación (modelo {e.Model}, {e.ChunkCount} tramo(s)):");
        Console.WriteLine($"   Prompt de sistema: {e.SystemPromptTokens:N0} tokens (escritura en caché en el 1.er tramo, lectura de caché en los demás)");
        Console.WriteLine($"   Entrada total:     {e.TotalInputTokens:N0} tokens" +
                          (e.GlossaryInputTokens > 0 ? $" (incluye glosario: {e.GlossaryInputTokens:N0})" : " (glosario ya existe)"));
        Console.WriteLine($"   Texto en inglés:   {e.EnglishTextTokens:N0} tokens → salida estimada ≈ {e.EstimatedOutputTokens:N0} tokens (×{CostEstimator.OutputFactor.ToString(Inv)})");
        Console.WriteLine($"   Coste estimado:    entrada ${e.InputCostUsd.ToString("0.0000", Inv)} + salida ${e.OutputCostUsd.ToString("0.0000", Inv)} = ${e.TotalCostUsd.ToString("0.00", Inv)} USD");
        Console.WriteLine("   (Estimación aproximada: no incluye reintentos; supone que la caché del prompt de sistema se aprovecha.)");
    }

    /// <returns>Stop = true si no tiene sentido seguir con más videos (límite de uso o sesión no iniciada).</returns>
    private static async Task<(TranslationStatus Status, string Detail, bool Stop)> TranslateOneAsync(
        ITranslationClient client, Settings s, TranslationPaths paths, CancellationToken ct)
    {
        var o = s.Options;
        Console.WriteLine($"Traduciendo {Path.GetFileName(paths.EnglishPath)} → {Path.GetFileName(paths.SpanishPath)} (backend {o.Backend}, modelo {o.Model}, esfuerzo {o.Effort}, tramos de {o.ChunkSize})");
        if (File.Exists(paths.SpanishPath) && !o.Force)
        {
            Console.WriteLine($"⏭️ Ya existe {Path.GetFileName(paths.SpanishPath)}; no se sobrescribe (usa --force).");
            return (TranslationStatus.SkippedExists, "ya existe .es.srt", false);
        }

        var log = new FileTranslationLog(AppPaths.Logs, Path.GetFileNameWithoutExtension(paths.EnglishPath));
        var translator = new SubtitleTranslator(client, o);
        var progress = new ConsoleProgress(s.IsClaudeCode);
        TranslationResult r;
        try
        {
            r = await translator.TranslateFileAsync(paths.EnglishPath, progress, log, ct);
        }
        catch (OperationCanceledException)
        {
            progress.End();
            Console.Error.WriteLine("⏸️  Cancelado. El progreso quedó guardado: relanza el mismo comando para continuar.");
            Console.Error.WriteLine($"   Log: {log.Path}");
            throw;
        }
        catch (TranslationClientException ex)
        {
            progress.End();
            Console.Error.WriteLine(ex.IsUsageLimit ? $"⏸️  {ex.Message}" : $"❌ {ex.Message}");
            Console.Error.WriteLine("   El progreso quedó guardado: relanza el mismo comando para continuar.");
            Console.Error.WriteLine($"   Log: {log.Path}");
            return (TranslationStatus.Failed, ex.IsUsageLimit ? "límite de uso alcanzado (reanudable)" : ex.Message, VideoWorkflow.StopsBatch(ex));
        }
        progress.End();

        foreach (var w in r.Warnings.Take(10))
            Console.WriteLine($"   ⚠️  {w}");
        if (r.Warnings.Count > 10)
            Console.WriteLine($"   … y {r.Warnings.Count - 10} advertencia(s) más (ver log).");

        var cost = $"tokens: entrada {r.Usage.InputTokens:N0}, salida {r.Usage.OutputTokens:N0}, caché leída {r.Usage.CacheReadInputTokens:N0}, caché escrita {r.Usage.CacheCreationInputTokens:N0} · {VideoWorkflow.FormatCost(r.EstimatedCostUsd, s.IsClaudeCode)}";
        switch (r.Status)
        {
            case TranslationStatus.Completed:
                Console.WriteLine($"✅ {r.OutputPath} ({r.BlockCount} bloques; validación: 0 errores, {r.Validation?.WarningCount ?? 0} advertencias de estilo)");
                switch (r.PlainCopy)
                {
                    case PlainCopyStatus.Created:
                        Console.WriteLine($"   Copia sin idioma creada: {r.PlainCopyPath}");
                        break;
                    case PlainCopyStatus.SkippedExists:
                        Console.WriteLine($"   ⚠️  Ya existe {Path.GetFileName(r.PlainCopyPath)} (sin idioma): no se tocó.");
                        break;
                    case PlainCopyStatus.Disabled:
                        Console.WriteLine("   Copia sin idioma desactivada (--no-plain-copy).");
                        break;
                }
                break;
            case TranslationStatus.Failed:
                Console.WriteLine($"❌ {r.Message}");
                foreach (var f in r.FailedBlocks.Take(20))
                    Console.WriteLine($"   - bloque {f.Id}: {f.Reason}");
                if (r.FailedBlocks.Count > 20)
                    Console.WriteLine($"   … y {r.FailedBlocks.Count - 20} más.");
                foreach (var e in r.Validation?.Errors.Take(20) ?? [])
                    Console.WriteLine($"   - {e.Message}");
                break;
            case TranslationStatus.PendingReview:
                Console.WriteLine($"📝 {r.Message}");
                Console.WriteLine($"   Revísala en la ventana (clic derecho → «Revisar traducción…») o acéptala tal cual con:");
                Console.WriteLine($"   subtool review \"{paths.EnglishPath}\" --accept");
                break;
            default:
                Console.WriteLine($"⏭️ {r.Message}");
                break;
        }
        Console.WriteLine($"   {cost}");
        if (r.LogPath is not null)
            Console.WriteLine($"   Log: {r.LogPath}");

        return (r.Status, VideoWorkflow.ResultDetail(r, s.IsClaudeCode), false);
    }

    // ------------------------------------------------------------- glossary

    public static async Task<int> GlossaryAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var a = ParsedArgs.Parse(args, TranslateOptions, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "regenerate-glossary" });
        a.RequirePositionals(1, "subtool glossary <archivo.en.srt> [--backend claude-code|api] [--model M] [--effort E] [--regenerate-glossary]");
        var paths = RequireEnglishSrt(a.Positionals[0]);
        var s = ParseSettings(a);

        if (!File.Exists(paths.GlossaryPath) || s.Options.RegenerateGlossary)
        {
            var client = CreateClient(s);
            if (client is null)
                return ExitCodes.Usage;
            Console.WriteLine($"Generando glosario de {Path.GetFileName(paths.EnglishPath)}…");
            var doc = await SrtParser.ParseFileAsync(paths.EnglishPath, ct);
            var translator = new SubtitleTranslator(client, s.Options);
            try
            {
                await translator.GetOrCreateGlossaryAsync(paths, doc, regenerate: true, ct);
            }
            catch (TranslationClientException ex)
            {
                Console.Error.WriteLine($"❌ {ex.Message}");
                return ExitCodes.Failure;
            }
        }
        else
        {
            Console.WriteLine("Glosario existente (usa --regenerate-glossary para regenerarlo):");
        }

        var g = TranslationPrompts.ParseGlossary(await File.ReadAllTextAsync(paths.GlossaryPath, Encoding.UTF8, ct));
        if (g is null)
        {
            Console.Error.WriteLine($"❌ El glosario no es JSON válido: {paths.GlossaryPath}");
            return ExitCodes.Failure;
        }
        Console.WriteLine($"Archivo: {paths.GlossaryPath} (puedes editarlo; se reutiliza al traducir)");
        Console.WriteLine();
        Console.WriteLine($"Términos ({g.Terms.Count}):");
        foreach (var t in g.Terms)
            Console.WriteLine($"   {t.En} → {t.Es}" + (string.IsNullOrWhiteSpace(t.Note) ? "" : $"  ({t.Note})"));
        Console.WriteLine($"Tratamiento ({g.Address.Count}):");
        foreach (var t in g.Address)
            Console.WriteLine($"   {t.A} → {t.B}: {t.Form}" + (string.IsNullOrWhiteSpace(t.Note) ? "" : $"  ({t.Note})"));
        return ExitCodes.Ok;
    }

    // ------------------------------------------------------------------ run

    public static async Task<int> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var a = ParsedArgs.Parse(args, TranslateOptions, TranslateFlags);
        a.RequirePositionals(1, "subtool run <carpeta|video> [opciones de translate]");
        var s = ParseSettings(a);
        var videos = LibraryScanner.FindVideos(a.Positionals[0]);
        if (videos.Count == 0)
        {
            Console.WriteLine($"No se encontraron videos ({VideoFormats.Description}) en: {a.Positionals[0]}");
            return ExitCodes.Ok;
        }

        var tools = new MediaToolkit();
        var scanner = new LibraryScanner(tools);
        ISubtitleTrackExtractor extractor = tools;
        ITranslationClient? client = null;
        var rows = new List<string[]>();

        foreach (var video in videos)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(video);
            Console.WriteLine();
            Console.WriteLine($"🎬 {name}");
            var st = await scanner.ScanVideoAsync(video, null, ct);

            var plan = VideoWorkflow.Plan(st, s.Options.Force);
            switch (plan.Kind)
            {
                case VideoPlanKind.InspectError:
                    rows.Add([name, "❌", plan.Reason]);
                    Console.WriteLine($"   ❌ {st.Error}");
                    continue;
                case VideoPlanKind.AlreadyTranslated:
                    rows.Add([name, "⏭️", plan.Reason]);
                    Console.WriteLine("   ⏭️ Ya tiene traducción al español.");
                    continue;
                case VideoPlanKind.PendingReview:
                    rows.Add([name, "📝", plan.Reason]);
                    Console.WriteLine($"   📝 Traducción pendiente de revisión (subtool review \"{st.EnglishPath}\" --accept para guardarla tal cual).");
                    continue;
                case VideoPlanKind.NoEnglish or VideoPlanKind.ImageOnly:
                    rows.Add([name, "❌", plan.Reason]);
                    Console.WriteLine($"   ❌ {plan.Reason}");
                    continue;
                case VideoPlanKind.UnsupportedFormat:
                    rows.Add([name, "❌", plan.Reason]);
                    Console.WriteLine($"   ❌ Solo se traducen .srt (la pista es {Path.GetExtension(st.EnglishPath)}).");
                    continue;
            }
            var track = st.Selection!.Track!;
            var englishPath = st.EnglishPath!;

            if (plan.NeedsExtraction)
            {
                if (s.DryRun)
                {
                    rows.Add([name, "⏭️", "dry-run: falta extraer el inglés"]);
                    Console.WriteLine("   ⏭️ dry-run: el inglés aún no está extraído (no se extrae en dry-run).");
                    continue;
                }
                var ex = await extractor.ExtractAsync(video, track.Id, englishPath, ct);
                if (ex.Status == ExtractionStatus.Failed)
                {
                    rows.Add([name, "❌", "extracción: " + ex.Message]);
                    Console.WriteLine($"   ❌ {ex.Message}");
                    continue;
                }
                Console.WriteLine($"   Extraído: {Path.GetFileName(englishPath)}");
            }

            client ??= CreateClient(s);
            if (client is null)
                return ExitCodes.Usage;

            var paths = TranslationPaths.FromEnglish(englishPath);
            if (s.DryRun)
            {
                var e = await new SubtitleTranslator(client, s.Options).EstimateAsync(paths.EnglishPath, ct);
                PrintEstimate(e, s);
                rows.Add([name, "✅", s.IsClaudeCode
                    ? $"{e.ChunkCount} tramo(s), ≈{e.TotalInputTokens:N0} tokens de entrada"
                    : $"estimado ${e.TotalCostUsd.ToString("0.00", Inv)}"]);
                continue;
            }

            var (status, detail, stop) = await TranslateOneAsync(client, s, paths, ct);
            rows.Add([name, status switch
            {
                TranslationStatus.Completed => "✅",
                TranslationStatus.SkippedExists => "⏭️",
                TranslationStatus.PendingReview => "📝",
                _ => "❌",
            }, detail]);
            if (stop)
            {
                Console.WriteLine("   ⏸️  Se detiene el lote: los videos restantes no se procesan. Relanza el comando más tarde.");
                break;
            }
        }

        Console.WriteLine();
        Console.WriteLine("Resumen:");
        PrintTable(["Video", "Estado", "Detalle"], rows);
        return rows.Any(r => r[1] == "❌") ? ExitCodes.Failure : ExitCodes.Ok;
    }

    private static void PrintTable(string[] headers, List<string[]> rows)
    {
        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        string Line(IReadOnlyList<string> cells) =>
            "   " + string.Join(" │ ", cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();
        Console.WriteLine(Line(headers));
        Console.WriteLine("   " + string.Join("─┼─", widths.Select(w => new string('─', w))));
        foreach (var r in rows)
            Console.WriteLine(Line(r));
    }

    // --------------------------------------------------------------- doctor

    public static async Task<int> DoctorAsync(IEnumerable<string> args, CancellationToken ct)
    {
        ParsedArgs.Parse(args, new HashSet<string>()).RequirePositionals(0, "subtool doctor");
        var checks = await new DoctorService().RunAsync(ct);
        foreach (var c in checks)
            Console.WriteLine(c.ToString());
        return DoctorService.AllOk(checks) ? ExitCodes.Ok : ExitCodes.Failure;
    }

    // ------------------------------------------------------------------ key

    public static int Key(IEnumerable<string> args)
    {
        var a = ParsedArgs.Parse(args, new HashSet<string>());
        a.RequirePositionals(1, "subtool key set|status|clear");
        var store = new ApiKeyStore();
        switch (a.Positionals[0].ToLowerInvariant())
        {
            case "set":
            {
                var key = ReadSecret("Pega la clave de API de Anthropic (no se mostrará) y pulsa Enter: ");
                if (string.IsNullOrWhiteSpace(key))
                {
                    Console.Error.WriteLine("❌ No se introdujo ninguna clave.");
                    return ExitCodes.Usage;
                }
                store.Save(key);
                var info = new ApiKeyInfo(ApiKeySource.EncryptedFile, key.Trim());
                Console.WriteLine($"✅ Clave guardada cifrada (DPAPI, usuario actual) en {store.FilePath} (…{info.Last4}).");
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyStore.EnvironmentVariable)))
                    Console.WriteLine($"   ⚠️  La variable {ApiKeyStore.EnvironmentVariable} está definida y tiene prioridad sobre la clave guardada.");
                return ExitCodes.Ok;
            }
            case "status":
            {
                var info = store.Resolve();
                Console.WriteLine(info.Source switch
                {
                    ApiKeySource.EnvironmentVariable => $"Clave de API: variable de entorno {ApiKeyStore.EnvironmentVariable} (…{info.Last4})",
                    ApiKeySource.EncryptedFile => $"Clave de API: archivo cifrado {store.FilePath} (…{info.Last4})",
                    _ => "Clave de API: no configurada. Usa 'subtool key set' o la variable de entorno ANTHROPIC_API_KEY.",
                });
                return ExitCodes.Ok;
            }
            case "clear":
                Console.WriteLine(store.Clear()
                    ? $"✅ Clave guardada borrada ({store.FilePath})."
                    : "No había clave guardada.");
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyStore.EnvironmentVariable)))
                    Console.WriteLine($"   ℹ️  La variable {ApiKeyStore.EnvironmentVariable} sigue definida (no se modifica).");
                return ExitCodes.Ok;
            default:
                throw new UsageException("Uso: subtool key set|status|clear");
        }
    }

    /// <summary>Lee la clave sin eco si hay consola; si la entrada está redirigida, lee una línea.</summary>
    private static string ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
            return Console.In.ReadLine() ?? "";
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter)
                break;
            if (k.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                    sb.Length--;
                continue;
            }
            if (!char.IsControl(k.KeyChar))
                sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}

/// <summary>Muestra el progreso en una sola línea. Con suscripción, el coste se muestra como equivalente de la API.</summary>
internal sealed class ConsoleProgress(bool subscription = false) : IProgress<TranslationProgress>
{
    private int _lastLength;
    private int _lastDone = -1;
    private bool _written;

    public void Report(TranslationProgress p)
    {
        var tramo = p.ChunkCount > 0 ? $"tramo {p.ChunkNumber}/{p.ChunkCount}" : p.Stage;
        var line = $"   {tramo} · bloques {p.BlocksDone}/{p.BlocksTotal} · tokens {p.Usage.InputTokens + p.Usage.CacheReadInputTokens + p.Usage.CacheCreationInputTokens:N0} ent / {p.Usage.OutputTokens:N0} sal · " +
                   (subscription
                       ? $"suscripción (≈${p.EstimatedCostUsd.ToString("0.0000", CultureInfo.InvariantCulture)} API)"
                       : $"~${p.EstimatedCostUsd.ToString("0.0000", CultureInfo.InvariantCulture)}");
        if (Console.IsOutputRedirected)
        {
            // Sin consola interactiva: una línea por avance real (no se repite el mismo estado).
            if (p.BlocksDone != _lastDone || p.ChunkCount == 0)
                Console.WriteLine(line);
            _lastDone = p.BlocksDone;
            return;
        }
        Console.Write("\r" + line.PadRight(_lastLength));
        _lastLength = line.Length;
        _written = true;
    }

    public void End()
    {
        if (_written && !Console.IsOutputRedirected)
            Console.WriteLine();
        _written = false;
    }
}
