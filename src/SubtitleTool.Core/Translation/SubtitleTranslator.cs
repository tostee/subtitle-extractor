using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SubtitleTool.Core.Review;
using SubtitleTool.Core.Srt;

namespace SubtitleTool.Core.Translation;

/// <summary>
/// Traduce un .en.srt por tramos secuenciales con glosario, validación, reintentos y reanudación.
/// Al modelo solo se envían {id, text}; los tiempos se conservan de las TimingLine originales.
/// </summary>
public sealed class SubtitleTranslator
{
    private readonly ITranslationClient _client;
    private readonly TranslationOptions _options;
    private readonly string _jobsRoot;
    private readonly ModelPricing _pricing;
    private readonly ReviewStore _reviews;
    private ITranslationLog _log = NullTranslationLog.Instance;

    private TokenUsage _usage = TokenUsage.Zero;
    private readonly List<string> _warnings = [];

    public SubtitleTranslator(ITranslationClient client, TranslationOptions options, string? jobsRoot = null, ReviewStore? reviews = null)
    {
        _reviews = reviews ?? ReviewStore.Default;
        if (options.ChunkSize < 1)
            throw new ArgumentException("El tamaño de tramo debe ser >= 1.", nameof(options));
        _client = client;
        _options = options;
        _jobsRoot = jobsRoot ?? AppPaths.Jobs;
        _pricing = ModelPricing.For(options.Model);
    }

    /// <summary>Divide en tramos de <paramref name="chunkSize"/> elementos.</summary>
    public static List<List<T>> SplitChunks<T>(IReadOnlyList<T> items, int chunkSize)
    {
        var result = new List<List<T>>();
        for (var i = 0; i < items.Count; i += chunkSize)
            result.Add(items.Skip(i).Take(chunkSize).ToList());
        return result;
    }

    /// <summary>Items de intercambio: id = posición 1-based del bloque; texto = líneas unidas con \n.</summary>
    public static List<TranslationItem> ToItems(SrtDocument doc) =>
        doc.Blocks.Select((b, i) => new TranslationItem(i + 1, string.Join("\n", b.Lines))).ToList();

    /// <summary>Reconstruye el documento traducido con índices y TimingLine originales.</summary>
    public static SrtDocument Assemble(SrtDocument original, IReadOnlyDictionary<int, string> translations)
    {
        var doc = new SrtDocument();
        for (var p = 0; p < original.Blocks.Count; p++)
        {
            var o = original.Blocks[p];
            var lines = o.IsEmpty
                ? o.Lines.ToList()
                : translations.TryGetValue(p + 1, out var t) ? NormalizeLines(t) : [];
            doc.Blocks.Add(new SrtBlock
            {
                Index = o.Index,
                TimingLine = o.TimingLine,
                Start = o.Start,
                End = o.End,
                Lines = lines,
            });
        }
        return doc;
    }

    /// <summary>Separa en líneas, quita líneas en blanco (romperían el SRT) y espacios finales.</summary>
    public static List<string> NormalizeLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)
            .ToList();

    // ------------------------------------------------------------------ glosario

    public async Task<Glossary> GetOrCreateGlossaryAsync(TranslationPaths paths, SrtDocument doc, bool regenerate, CancellationToken ct)
    {
        if (!regenerate && File.Exists(paths.GlossaryPath))
        {
            var existing = TranslationPrompts.ParseGlossary(await File.ReadAllTextAsync(paths.GlossaryPath, Encoding.UTF8, ct).ConfigureAwait(false));
            if (existing is null)
                throw new InvalidDataException($"El glosario no es JSON válido: {paths.GlossaryPath}. Corrígelo o usa --regenerate-glossary.");
            _log.Write($"Glosario existente reutilizado: {paths.GlossaryPath} ({existing.Terms.Count} términos, {existing.Address.Count} tratamientos)");
            return existing;
        }

        var items = ToItems(doc).Where(i => i.Text.Trim().Length > 0);
        var request = new GlossaryRequest(TranslationPrompts.GlossarySystemPrompt, TranslationPrompts.BuildGlossaryUserMessage(items));
        Glossary? glossary = null;
        for (var attempt = 0; attempt <= _options.MaxInvalidJsonRetries && glossary is null; attempt++)
        {
            var sw = Stopwatch.StartNew();
            var resp = await _client.BuildGlossaryAsync(request, ct).ConfigureAwait(false);
            AddUsage(resp.Usage);
            _log.Write($"Glosario: intento {attempt + 1}, stop_reason={resp.StopReason}, {FormatUsage(resp.Usage)}, {sw.ElapsedMilliseconds} ms" +
                       (resp.Glossary is null ? ", respuesta no válida" : ""));
            glossary = resp.Glossary;
        }
        if (glossary is null)
            throw new TranslationClientException("No se pudo generar el glosario (respuesta no válida tras los reintentos).");

        // Con la respuesta ya obtenida, la escritura no se cancela a medias: se escribe a un temporal y se reemplaza
        // de una vez, para que una cancelación nunca deje el glosario truncado.
        var temp = paths.GlossaryPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(glossary, TranslationPrompts.IndentedJsonOptions), new UTF8Encoding(false));
        File.Move(temp, paths.GlossaryPath, overwrite: true);
        _log.Write($"Glosario guardado: {paths.GlossaryPath} ({glossary.Terms.Count} términos, {glossary.Address.Count} tratamientos)");
        return glossary;
    }

    // ------------------------------------------------------------------ estimación

    /// <summary>Cuenta tokens (sin traducir) y estima el coste.</summary>
    public async Task<CostEstimate> EstimateAsync(string englishPath, CancellationToken ct = default)
    {
        var paths = TranslationPaths.FromEnglish(englishPath);
        var doc = await SrtParser.ParseFileAsync(paths.EnglishPath, ct).ConfigureAwait(false);
        var all = ToItems(doc).Where(i => !doc.Blocks[i.Id - 1].IsEmpty).ToList();

        Glossary glossary = new();
        long glossaryTokens = 0;
        if (File.Exists(paths.GlossaryPath) && !_options.RegenerateGlossary)
        {
            glossary = TranslationPrompts.ParseGlossary(await File.ReadAllTextAsync(paths.GlossaryPath, ct).ConfigureAwait(false)) ?? new Glossary();
        }
        else
        {
            glossaryTokens = await _client.CountTokensAsync(TranslationPrompts.GlossarySystemPrompt,
                TranslationPrompts.BuildGlossaryUserMessage(all), ct).ConfigureAwait(false);
        }

        // Tokens del prompt de sistema = (sistema + mensaje mínimo) − (mensaje mínimo).
        const string probe = ".";
        var withSystem = await _client.CountTokensAsync(TranslationPrompts.SystemPrompt, probe, ct).ConfigureAwait(false);
        var without = await _client.CountTokensAsync(null, probe, ct).ConfigureAwait(false);
        var systemTokens = Math.Max(0, withSystem - without);

        var chunks = SplitChunks(all, _options.ChunkSize);
        var chunkTokens = new List<long>();
        IReadOnlyList<TranslationItem> context = [];
        foreach (var chunk in chunks)
        {
            // El contexto real será el texto ya traducido; aquí se aproxima con el inglés.
            var msg = TranslationPrompts.BuildChunkUserMessage(glossary, context, chunk);
            chunkTokens.Add(await _client.CountTokensAsync(TranslationPrompts.SystemPrompt, msg, ct).ConfigureAwait(false));
            context = chunk.TakeLast(_options.ContextSize).ToList();
        }

        var englishTokens = all.Count == 0 ? 0 : await _client.CountTokensAsync(null, TranslationPrompts.ItemsJson(all), ct).ConfigureAwait(false);
        return CostEstimator.Estimate(_options.Model, systemTokens, chunkTokens, englishTokens, glossaryTokens);
    }

    // ------------------------------------------------------------------ traducción

    public async Task<TranslationResult> TranslateFileAsync(string englishPath, IProgress<TranslationProgress>? progress = null,
        ITranslationLog? log = null, CancellationToken ct = default)
    {
        _log = log ?? NullTranslationLog.Instance;
        _usage = TokenUsage.Zero;
        _warnings.Clear();

        var paths = TranslationPaths.FromEnglish(englishPath);
        if (!File.Exists(paths.EnglishPath))
            throw new FileNotFoundException($"No existe el archivo: {paths.EnglishPath}", paths.EnglishPath);

        if (File.Exists(paths.SpanishPath) && !_options.Force)
        {
            return new TranslationResult
            {
                Status = TranslationStatus.SkippedExists,
                OutputPath = paths.SpanishPath,
                Message = "La traducción ya existe; no se sobrescribe (usa --force).",
            };
        }

        switch (_reviews.GetState(paths.EnglishPath))
        {
            case DraftState.Valid when !_options.Force:
                return Result(TranslationStatus.PendingReview, paths, 0, [], null,
                    "Ya hay una traducción pendiente de revisión; no se vuelve a traducir (usa --force para descartarla).")
                    with { OutputPath = _reviews.DraftPath(paths.EnglishPath) };
            case DraftState.Stale:
                // Se hizo con otro inglés: ya no encaja con este, así que no sirve.
                _warnings.Add("Había un borrador de revisión hecho con otra versión del subtítulo en inglés: se descarta.");
                _reviews.Delete(paths.EnglishPath);
                break;
            // Válido con --force: se conserva hasta que haya un borrador o un .es.srt nuevo (si se cancela, no se pierde).
        }

        _log.Write($"Trabajo: {paths.EnglishPath} → {paths.SpanishPath}; backend={_options.Backend}, modelo={_options.Model}, esfuerzo={_options.Effort}, tramo={_options.ChunkSize}, prompt v{TranslationPrompts.PromptVersion}");

        var doc = await SrtParser.ParseFileAsync(paths.EnglishPath, ct).ConfigureAwait(false);
        var allItems = ToItems(doc);
        var store = PrepareStore(paths);

        Report(progress, "glosario", 0, 0, 0, allItems.Count);
        var glossary = await GetOrCreateGlossaryAsync(paths, doc, _options.RegenerateGlossary, ct).ConfigureAwait(false);

        var chunks = SplitChunks(allItems, _options.ChunkSize);
        var translations = new Dictionary<int, string>();
        var failures = new List<FailedBlock>();
        IReadOnlyList<TranslationItem> context = [];
        var done = 0;

        for (var c = 0; c < chunks.Count; c++)
        {
            ct.ThrowIfCancellationRequested();
            var chunkNumber = c + 1;
            var chunk = chunks[c];
            // Los bloques vacíos en el original no se envían: se quedan vacíos.
            var toTranslate = chunk.Where(i => !doc.Blocks[i.Id - 1].IsEmpty).ToList();
            var expected = toTranslate.Select(i => i.Id).ToHashSet();

            var state = store.LoadChunk(chunkNumber) ?? new ChunkState();
            var accepted = state.Items.Where(i => expected.Contains(i.Id) && i.Text.Trim().Length > 0)
                .GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First().Text);

            if (accepted.Count == expected.Count)
            {
                _log.Write($"Tramo {chunkNumber}/{chunks.Count}: ya completo (reanudación), se salta");
            }
            else
            {
                var pending = toTranslate.Where(i => !accepted.ContainsKey(i.Id)).ToList();
                Report(progress, "traduciendo", chunkNumber, chunks.Count, done, allItems.Count);
                var chunkFailures = new List<FailedBlock>();
                try
                {
                    await TranslateItemsAsync(chunkNumber, pending, context, glossary, accepted, chunkFailures, refusalRetried: false, ct).ConfigureAwait(false);
                }
                finally
                {
                    // Se guarda siempre lo aceptado (también si hubo excepción o cancelación) para reanudar.
                    store.SaveChunk(chunkNumber, new ChunkState
                    {
                        Complete = accepted.Count == expected.Count,
                        Items = toTranslate.Where(i => accepted.ContainsKey(i.Id)).Select(i => new TranslationItem(i.Id, accepted[i.Id])).ToList(),
                    });
                }
                failures.AddRange(chunkFailures);
            }

            foreach (var kv in accepted)
                translations[kv.Key] = kv.Value;
            done += chunk.Count;
            context = toTranslate.Where(i => accepted.ContainsKey(i.Id))
                .Select(i => new TranslationItem(i.Id, accepted[i.Id]))
                .TakeLast(_options.ContextSize).ToList();
            Report(progress, "traduciendo", chunkNumber, chunks.Count, done, allItems.Count);
        }

        if (failures.Count > 0)
        {
            foreach (var f in failures)
                _log.Write($"Bloque {f.Id} fallido: {f.Reason}");
            _log.Write($"Resultado: {failures.Count} bloque(s) sin traducir; no se escribe el .es.srt; el estado queda en {store.Directory}");
            return Result(TranslationStatus.Failed, paths, doc.Blocks.Count, failures, null,
                $"{failures.Count} bloque(s) no se pudieron traducir. No se escribió el .es.srt; relanza el comando para reintentar solo lo pendiente.");
        }

        var translated = Assemble(doc, translations);
        var report = SrtValidator.Validate(doc, translated);
        if (report.HasErrors)
        {
            foreach (var e in report.Errors)
                _log.Write($"Validación: {e.Message}");
            return Result(TranslationStatus.Failed, paths, doc.Blocks.Count, failures, report,
                $"La validación final encontró {report.ErrorCount} error(es). No se escribió el .es.srt.");
        }

        if (_options.ReviewBeforeSave)
        {
            await _reviews.SaveDraftAsync(paths, translated, !_options.NoPlainCopy, edited: [],
                replacesExisting: File.Exists(paths.SpanishPath), ct: ct).ConfigureAwait(false);
            store.Delete();
            var draft = _reviews.DraftPath(paths.EnglishPath);
            _log.Write($"Borrador para revisar: {draft} ({translated.Blocks.Count} bloques, {report.WarningCount} advertencias de estilo). " +
                       $"El .es.srt se escribirá al guardar la revisión. {FormatUsage(_usage)}, costo estimado ${Cost(_usage):0.0000}");
            return Result(TranslationStatus.PendingReview, paths, doc.Blocks.Count, failures, report,
                "Traducción lista para revisar antes de guardar.") with { OutputPath = draft };
        }

        await SrtWriter.WriteFileAsync(paths.SpanishPath, translated, ct).ConfigureAwait(false);
        if (_reviews.HasDraft(paths.EnglishPath))
            _reviews.Delete(paths.EnglishPath); // un borrador anterior ya no sirve: hay .es.srt nuevo (--force)
        _log.Write($"Escrito: {paths.SpanishPath} ({translated.Blocks.Count} bloques, {report.WarningCount} advertencias de estilo)");

        var plain = PlainCopyStatus.Disabled;
        if (!_options.NoPlainCopy)
        {
            if (File.Exists(paths.PlainCopyPath))
            {
                plain = PlainCopyStatus.SkippedExists;
                _log.Write($"Ya existe un .srt sin idioma; no se toca: {paths.PlainCopyPath}");
            }
            else
            {
                await SrtWriter.WriteFileAsync(paths.PlainCopyPath, translated, ct).ConfigureAwait(false);
                plain = PlainCopyStatus.Created;
                _log.Write($"Copia sin idioma creada: {paths.PlainCopyPath}");
            }
        }

        store.Delete();
        _log.Write($"Completado. {FormatUsage(_usage)}, costo estimado ${Cost(_usage):0.0000}");
        return Result(TranslationStatus.Completed, paths, doc.Blocks.Count, failures, report, "Traducción completada.") with { PlainCopy = plain };
    }

    private JobStore PrepareStore(TranslationPaths paths)
    {
        var store = new JobStore(_jobsRoot, paths.EnglishPath);
        if (_options.Restart && store.Exists)
        {
            _log.Write("--restart: se borra el estado anterior");
            store.Delete();
        }
        var job = store.LoadJob();
        var jobBackend = string.IsNullOrEmpty(job?.Backend) ? TranslationBackends.Api : job.Backend;
        if (job is not null &&
            (jobBackend != _options.Backend || job.Model != _options.Model || job.ChunkSize != _options.ChunkSize || job.PromptVersion != TranslationPrompts.PromptVersion))
        {
            var msg = $"El trabajo guardado usaba otra configuración (backend {jobBackend}, modelo {job.Model}, tramo {job.ChunkSize}, prompt v{job.PromptVersion}); se empieza de cero.";
            _warnings.Add(msg);
            _log.Write(msg);
            store.Delete();
            job = null;
        }
        if (job is null)
        {
            store.SaveJob(new JobInfo
            {
                Source = paths.EnglishPath,
                Backend = _options.Backend,
                Model = _options.Model,
                ChunkSize = _options.ChunkSize,
                PromptVersion = TranslationPrompts.PromptVersion,
                Created = DateTimeOffset.Now,
            });
        }
        else
        {
            _log.Write($"Reanudando trabajo guardado en {store.Directory}");
        }
        return store;
    }

    /// <summary>
    /// Traduce un conjunto de bloques: reintenta solo ids faltantes/vacíos (hasta N veces), divide en dos si
    /// max_tokens, divide y reintenta una vez si hay rechazo, y reintenta si el JSON no es válido.
    /// </summary>
    private async Task TranslateItemsAsync(int chunkNumber, List<TranslationItem> items, IReadOnlyList<TranslationItem> context,
        Glossary glossary, Dictionary<int, string> accepted, List<FailedBlock> failures, bool refusalRetried, CancellationToken ct)
    {
        var pending = items;
        var missingRetries = 0;
        var invalidJsonRetries = 0;

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var request = new ChunkRequest
            {
                ChunkNumber = chunkNumber,
                SystemPrompt = TranslationPrompts.SystemPrompt,
                UserMessage = TranslationPrompts.BuildChunkUserMessage(glossary, context, pending),
                Items = pending,
                Context = context,
            };

            var sw = Stopwatch.StartNew();
            ChunkResponse resp;
            try
            {
                resp = await _client.TranslateChunkAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Write($"Tramo {chunkNumber} ids {Ranges(pending)}: ERROR tras {sw.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            AddUsage(resp.Usage);
            _log.Write($"Tramo {chunkNumber} ids {Ranges(pending)} ({pending.Count}): stop_reason={resp.StopReason}, {FormatUsage(resp.Usage)}, {sw.ElapsedMilliseconds} ms");

            if (resp.StopReason == StopReasons.MaxTokens)
            {
                if (pending.Count == 1)
                {
                    Fail(failures, pending, "max_tokens con un solo bloque");
                    return;
                }
                _log.Write($"Tramo {chunkNumber}: max_tokens → se divide en dos");
                var (a, b) = Halves(pending);
                await TranslateItemsAsync(chunkNumber, a, context, glossary, accepted, failures, refusalRetried, ct).ConfigureAwait(false);
                await TranslateItemsAsync(chunkNumber, b, context, glossary, accepted, failures, refusalRetried, ct).ConfigureAwait(false);
                return;
            }

            if (resp.StopReason == StopReasons.Refusal)
            {
                var detail = $"rechazo del modelo (categoría: {resp.RefusalCategory ?? "?"}; explicación: {resp.RefusalExplanation ?? "-"})";
                _log.Write($"Tramo {chunkNumber} ids {Ranges(pending)}: {detail}");
                if (refusalRetried)
                {
                    Fail(failures, pending, detail);
                    return;
                }
                if (pending.Count == 1)
                {
                    await TranslateItemsAsync(chunkNumber, pending, context, glossary, accepted, failures, refusalRetried: true, ct).ConfigureAwait(false);
                    return;
                }
                var (a, b) = Halves(pending);
                await TranslateItemsAsync(chunkNumber, a, context, glossary, accepted, failures, refusalRetried: true, ct).ConfigureAwait(false);
                await TranslateItemsAsync(chunkNumber, b, context, glossary, accepted, failures, refusalRetried: true, ct).ConfigureAwait(false);
                return;
            }

            if (resp.Items is null)
            {
                invalidJsonRetries++;
                _log.Write($"Tramo {chunkNumber}: respuesta JSON no válida (intento {invalidJsonRetries})");
                if (invalidJsonRetries > _options.MaxInvalidJsonRetries)
                {
                    Fail(failures, pending, "respuesta JSON no válida tras los reintentos");
                    return;
                }
                continue;
            }

            var wanted = pending.Select(i => i.Id).ToHashSet();
            var seen = new HashSet<int>();
            foreach (var item in resp.Items)
            {
                if (!wanted.Contains(item.Id))
                {
                    Warn($"Tramo {chunkNumber}: id {item.Id} no pedido; se ignora");
                    continue;
                }
                if (!seen.Add(item.Id))
                {
                    Warn($"Tramo {chunkNumber}: id {item.Id} duplicado; se usa el primero");
                    continue;
                }
                if (NormalizeLines(item.Text).Count > 0)
                    accepted[item.Id] = item.Text;
            }

            pending = pending.Where(i => !accepted.ContainsKey(i.Id)).ToList();
            if (pending.Count == 0)
                return;

            missingRetries++;
            _log.Write($"Tramo {chunkNumber}: faltan o están vacíos los ids {Ranges(pending)} (reintento {missingRetries}/{_options.MaxMissingRetries})");
            if (missingRetries > _options.MaxMissingRetries)
            {
                Fail(failures, pending, $"faltante o vacío tras {_options.MaxMissingRetries} reintentos");
                return;
            }
        }
    }

    private static (List<TranslationItem>, List<TranslationItem>) Halves(List<TranslationItem> items)
    {
        var mid = items.Count / 2;
        return (items.Take(mid).ToList(), items.Skip(mid).ToList());
    }

    private static void Fail(List<FailedBlock> failures, IEnumerable<TranslationItem> items, string reason) =>
        failures.AddRange(items.Select(i => new FailedBlock(i.Id, reason)));

    private void Warn(string message)
    {
        _warnings.Add(message);
        _log.Write("Advertencia: " + message);
    }

    private void AddUsage(TokenUsage usage) => _usage = _usage.Add(usage);

    /// <summary>Coste informado por el backend si lo hay (Claude Code); si no, calculado con la tabla de precios.</summary>
    private decimal Cost(TokenUsage usage) => usage.ReportedCostUsd > 0 ? usage.ReportedCostUsd : _pricing.Cost(usage);

    private void Report(IProgress<TranslationProgress>? progress, string stage, int chunk, int chunks, int done, int total) =>
        progress?.Report(new TranslationProgress(stage, chunk, chunks, done, total, _usage, Cost(_usage)));

    private TranslationResult Result(TranslationStatus status, TranslationPaths paths, int blocks, List<FailedBlock> failures,
        ValidationReport? report, string message) => new()
    {
        Status = status,
        OutputPath = paths.SpanishPath,
        PlainCopyPath = paths.PlainCopyPath,
        BlockCount = blocks,
        FailedBlocks = failures,
        Validation = report,
        Usage = _usage,
        EstimatedCostUsd = Cost(_usage),
        Warnings = _warnings.ToList(),
        Message = message,
        LogPath = _log.Path,
    };

    private static string FormatUsage(TokenUsage u) =>
        $"tokens entrada={u.InputTokens} salida={u.OutputTokens} caché_lectura={u.CacheReadInputTokens} caché_escritura={u.CacheCreationInputTokens}" +
        (u.ReportedCostUsd > 0 ? $" total_cost_usd={u.ReportedCostUsd.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}" : "");

    /// <summary>Ids compactados en rangos: "1-5,8,10-12".</summary>
    public static string Ranges(IEnumerable<TranslationItem> items)
    {
        var ids = items.Select(i => i.Id).OrderBy(i => i).ToList();
        var parts = new List<string>();
        for (var i = 0; i < ids.Count;)
        {
            var j = i;
            while (j + 1 < ids.Count && ids[j + 1] == ids[j] + 1)
                j++;
            parts.Add(i == j ? $"{ids[i]}" : $"{ids[i]}-{ids[j]}");
            i = j + 1;
        }
        return string.Join(",", parts);
    }
}
