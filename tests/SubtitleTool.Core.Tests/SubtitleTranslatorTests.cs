using System.Text;
using SubtitleTool.Core.Srt;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Tests;

public sealed class SubtitleTranslatorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _jobs;

    public SubtitleTranslatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "subtool-tests-" + Guid.NewGuid().ToString("N"));
        _jobs = Path.Combine(_dir, "_jobs");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>SRT de ejemplo: etiquetas, guiones, varias líneas, un bloque vacío y ajustes de posición.</summary>
    public static string MakeSrt(int blocks)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= blocks; i++)
        {
            var start = TimeSpan.FromMilliseconds(1000 + i * 2500);
            var end = start + TimeSpan.FromMilliseconds(1800 + i % 7 * 10);
            sb.Append(i).Append("\r\n");
            sb.Append($"{Fmt(start)} --> {Fmt(end)}");
            if (i % 50 == 0)
                sb.Append(" X1:100 X2:200 Y1:10 Y2:20");
            sb.Append("\r\n");
            if (i == 77)
            {
                // bloque vacío: sin líneas de texto
            }
            else if (i % 10 == 0)
                sb.Append("- Line ").Append(i).Append(".\r\n- Answer ").Append(i).Append(".\r\n");
            else if (i % 7 == 0)
                sb.Append("<i>Italic ").Append(i).Append("</i>\r\n");
            else if (i % 13 == 0)
                sb.Append("{\\an8}[music playing]\r\n");
            else
                sb.Append("Line ").Append(i).Append('\r').Append('\n');
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Fmt(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";

    private string WriteEnglish(int blocks, string name = "Movie.en.srt")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, MakeSrt(blocks), new UTF8Encoding(false));
        return path;
    }

    private SubtitleTranslator Translator(FakeTranslationClient fake, TranslationOptions? options = null) =>
        new(fake, options ?? new TranslationOptions(), _jobs);

    private static List<int> Ids(ChunkRequest r) => r.Items.Select(i => i.Id).ToList();

    // ------------------------------------------------------------ tramos y ensamblado

    [Fact]
    public void SplitChunks_SizesAreCorrect()
    {
        var chunks = SubtitleTranslator.SplitChunks(Enumerable.Range(1, 400).ToList(), 150);

        Assert.Equal([150, 150, 100], chunks.Select(c => c.Count));
        Assert.Equal(1, chunks[0][0]);
        Assert.Equal(301, chunks[2][0]);
    }

    [Fact]
    public async Task ChunkingAndAssembly_KeepsTimingsAndNumbering()
    {
        var en = WriteEnglish(400);
        var fake = new FakeTranslationClient();

        var r = await Translator(fake).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Completed, r.Status);
        // 3 tramos (150/150/100); el bloque vacío 77 no se envía.
        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal(149, fake.Requests[0].Items.Count);
        Assert.DoesNotContain(77, Ids(fake.Requests[0]));
        Assert.Equal(100, fake.Requests[2].Items.Count);

        var original = SrtParser.ParseFile(en);
        var translated = SrtParser.ParseFile(r.OutputPath);
        Assert.Equal(original.Blocks.Count, translated.Blocks.Count);
        for (var i = 0; i < original.Blocks.Count; i++)
        {
            Assert.Equal(original.Blocks[i].Index, translated.Blocks[i].Index);
            Assert.Equal(original.Blocks[i].TimingLine, translated.Blocks[i].TimingLine);
            Assert.Equal(original.Blocks[i].Start, translated.Blocks[i].Start);
            Assert.Equal(original.Blocks[i].End, translated.Blocks[i].End);
        }
        Assert.Equal(["ES: - Line 10.", "- Answer 10."], translated.Blocks[9].Lines);
        Assert.Empty(translated.Blocks[76].Lines);
        Assert.False(SrtValidator.Validate(original, translated).HasErrors);
    }

    [Fact]
    public void Assemble_UsesOriginalTimingLines()
    {
        var doc = SrtParser.Parse(MakeSrt(5));
        var tr = new Dictionary<int, string> { [1] = "a", [2] = "b\nc", [3] = "d", [4] = "e", [5] = "f\n\n" };

        var result = SubtitleTranslator.Assemble(doc, tr);

        Assert.Equal(doc.Blocks.Select(b => b.TimingLine), result.Blocks.Select(b => b.TimingLine));
        Assert.Equal(["b", "c"], result.Blocks[1].Lines);
        Assert.Equal(["f"], result.Blocks[4].Lines);
    }

    // ------------------------------------------------------------ ids faltantes

    [Fact]
    public async Task MissingIds_OnlyThoseAreRetried()
    {
        var en = WriteEnglish(20);
        var fake = new FakeTranslationClient
        {
            Handler = (req, call) => call == 0
                ? FakeTranslationClient.Ok(req.Items.Where(i => i.Id is not (5 or 7)))
                : null,
        };

        var r = await Translator(fake).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Completed, r.Status);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal([5, 7], Ids(fake.Requests[1]));
    }

    [Fact]
    public async Task EmptyText_IsTreatedAsMissing()
    {
        var en = WriteEnglish(10);
        var fake = new FakeTranslationClient
        {
            Handler = (req, call) => call == 0
                ? new ChunkResponse { Items = req.Items.Select(i => new TranslationItem(i.Id, i.Id == 3 ? "  " : "x")).ToList() }
                : null,
        };

        var r = await Translator(fake).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Completed, r.Status);
        Assert.Equal([3], Ids(fake.Requests[1]));
    }

    [Fact]
    public async Task MissingIds_AfterTwoRetries_AreMarkedFailed_AndNoOutputWritten()
    {
        var en = WriteEnglish(20);
        var fake = new FakeTranslationClient
        {
            Handler = (req, _) => FakeTranslationClient.Ok(req.Items.Where(i => i.Id != 5)),
        };

        var r = await Translator(fake).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Failed, r.Status);
        Assert.Equal(3, fake.Requests.Count); // 1 intento + 2 reintentos
        Assert.Equal([5], Ids(fake.Requests[2]));
        var f = Assert.Single(r.FailedBlocks);
        Assert.Equal(5, f.Id);
        Assert.False(File.Exists(r.OutputPath));
        Assert.False(File.Exists(Path.Combine(_dir, "Movie.srt")));
        Assert.True(Directory.Exists(new JobStore(_jobs, en).Directory)); // estado para reanudar
    }

    [Fact]
    public async Task ExtraAndDuplicateIds_AreIgnoredWithWarning()
    {
        var en = WriteEnglish(5);
        var fake = new FakeTranslationClient
        {
            Handler = (req, _) => new ChunkResponse
            {
                Items = req.Items.Select(i => new TranslationItem(i.Id, "x" + i.Id))
                    .Append(new TranslationItem(999, "extra"))
                    .Append(new TranslationItem(1, "duplicado"))
                    .ToList(),
            },
        };

        var r = await Translator(fake).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Completed, r.Status);
        Assert.Contains(r.Warnings, w => w.Contains("999"));
        Assert.Equal(["x1"], SrtParser.ParseFile(r.OutputPath).Blocks[0].Lines);
    }

    [Fact]
    public async Task InvalidJson_IsRetried()
    {
        var en = WriteEnglish(5);
        var fake = new FakeTranslationClient
        {
            Handler = (_, call) => call == 0 ? new ChunkResponse { Items = null } : null,
        };

        var r = await Translator(fake).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Completed, r.Status);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal(5, fake.Requests[1].Items.Count);
    }

    // ------------------------------------------------------------ max_tokens y rechazo

    [Fact]
    public async Task MaxTokens_SplitsChunk()
    {
        var en = WriteEnglish(100);
        var fake = new FakeTranslationClient
        {
            Handler = (req, _) => req.Items.Count > 30 ? new ChunkResponse { StopReason = StopReasons.MaxTokens } : null,
        };

        var r = await Translator(fake, new TranslationOptions { ChunkSize = 100 }).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Completed, r.Status);
        // 99 bloques con texto (el 77 está vacío): 99 → 49 + 50 → 24 + 25 + 25 + 25.
        Assert.Equal(99, fake.Requests[0].Items.Count);
        Assert.Equal(49, fake.Requests[1].Items.Count);
        Assert.All(fake.Requests.Where(q => q.Items.Count <= 30), q => Assert.True(q.Items.Count >= 24));
        Assert.Equal(99, fake.Requests.Where(q => q.Items.Count <= 30).Sum(q => q.Items.Count));
    }

    [Fact]
    public async Task Refusal_SplitsInHalvesAndRetries()
    {
        var en = WriteEnglish(20);
        var fake = new FakeTranslationClient
        {
            Handler = (_, call) => call == 0 ? new ChunkResponse { StopReason = StopReasons.Refusal, RefusalCategory = "cyber" } : null,
        };

        var r = await Translator(fake).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Completed, r.Status);
        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal(Enumerable.Range(1, 10), Ids(fake.Requests[1]));
        Assert.Equal(Enumerable.Range(11, 10), Ids(fake.Requests[2]));
    }

    [Fact]
    public async Task Refusal_Persistent_RecordsFailureAndContinues()
    {
        var en = WriteEnglish(40);
        var fake = new FakeTranslationClient
        {
            Handler = (req, _) => req.Items.Any(i => i.Id == 3)
                ? new ChunkResponse { StopReason = StopReasons.Refusal, RefusalCategory = "violence", RefusalExplanation = "test" }
                : null,
        };

        var r = await Translator(fake, new TranslationOptions { ChunkSize = 20 }).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Failed, r.Status);
        // tramo 1 rechazado → mitades 1-10 (rechazada otra vez → fallida) y 11-20 (ok); tramo 2 sigue.
        Assert.Equal(Enumerable.Range(1, 10), r.FailedBlocks.Select(f => f.Id));
        Assert.All(r.FailedBlocks, f => Assert.Contains("violence", f.Reason));
        Assert.Contains(fake.Requests, q => q.ChunkNumber == 2);
        Assert.Equal(4, fake.Requests.Count);
        Assert.False(File.Exists(r.OutputPath));
    }

    // ------------------------------------------------------------ reanudación

    [Fact]
    public async Task Resume_AfterExceptionInChunk3_DoesNotRequestChunks1And2Again()
    {
        var en = WriteEnglish(100);
        var options = new TranslationOptions { ChunkSize = 20 };
        var failing = new FakeTranslationClient
        {
            Handler = (req, _) => req.ChunkNumber == 3 ? throw new TranslationClientException("fallo simulado") : null,
        };

        await Assert.ThrowsAsync<TranslationClientException>(() => Translator(failing, options).TranslateFileAsync(en));
        Assert.Equal([1, 2, 3], failing.Requests.Select(q => q.ChunkNumber));
        Assert.False(File.Exists(Path.Combine(_dir, "Movie.es.srt")));

        var second = new FakeTranslationClient();
        var r = await Translator(second, options).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Completed, r.Status);
        Assert.Equal([3, 4, 5], second.Requests.Select(q => q.ChunkNumber));
        Assert.Empty(second.GlossaryRequests); // el glosario ya existía
        // El contexto del tramo 3 viene del tramo 2 guardado en disco.
        Assert.Contains("ES: Line 38", second.Requests[0].UserMessage);
        Assert.False(Directory.Exists(new JobStore(_jobs, en).Directory)); // estado borrado al terminar
    }

    [Fact]
    public async Task Restart_DiscardsSavedState()
    {
        var en = WriteEnglish(60);
        var options = new TranslationOptions { ChunkSize = 20 };
        var failing = new FakeTranslationClient
        {
            Handler = (req, _) => req.ChunkNumber == 2 ? throw new TranslationClientException("x") : null,
        };
        await Assert.ThrowsAsync<TranslationClientException>(() => Translator(failing, options).TranslateFileAsync(en));

        var second = new FakeTranslationClient();
        await Translator(second, options with { Restart = true }).TranslateFileAsync(en);

        Assert.Equal([1, 2, 3], second.Requests.Select(q => q.ChunkNumber));
    }

    [Fact]
    public async Task Cancellation_LeavesStateForResume()
    {
        var en = WriteEnglish(60);
        var options = new TranslationOptions { ChunkSize = 20 };
        using var cts = new CancellationTokenSource();
        var fake = new FakeTranslationClient
        {
            Handler = (req, _) =>
            {
                if (req.ChunkNumber == 2)
                    cts.Cancel();
                return null;
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Translator(fake, options).TranslateFileAsync(en, ct: cts.Token));

        var second = new FakeTranslationClient();
        var r = await Translator(second, options).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Completed, r.Status);
        Assert.Equal([3], second.Requests.Select(q => q.ChunkNumber));
    }

    // ------------------------------------------------------------ contexto y prompt

    [Fact]
    public async Task ChunkMessage_IncludesLast10TranslatedBlocksOfPreviousChunk_AndGlossary()
    {
        var en = WriteEnglish(45);
        var fake = new FakeTranslationClient();

        await Translator(fake, new TranslationOptions { ChunkSize = 20 }).TranslateFileAsync(en);

        var msg2 = fake.Requests[1].UserMessage;
        Assert.Equal(Enumerable.Range(11, 10), fake.Requests[1].Context.Select(i => i.Id));
        for (var i = 11; i <= 19; i++)
            Assert.Contains($"\"id\":{i},", msg2);
        Assert.Contains("ES: - Line 20.\\n- Answer 20.", msg2);
        Assert.DoesNotContain("\"id\":10,", msg2);
        Assert.Contains("Odiseo", msg2);
        Assert.Contains("Telemachus", msg2);
        Assert.Contains("solo referencia", msg2);
        // El primer tramo no lleva contexto.
        Assert.Empty(fake.Requests[0].Context);
        Assert.DoesNotContain("CONTEXTO PREVIO", fake.Requests[0].UserMessage);
        // El glosario se guardó junto al .en.srt.
        Assert.True(File.Exists(Path.Combine(_dir, "Movie.glossary.json")));
    }

    [Fact]
    public async Task SystemPrompt_IsByteIdenticalAcrossCalls()
    {
        var en = WriteEnglish(100);
        var fake = new FakeTranslationClient
        {
            Handler = (_, call) => call == 1 ? new ChunkResponse { StopReason = StopReasons.MaxTokens } : null,
        };

        await Translator(fake, new TranslationOptions { ChunkSize = 25 }).TranslateFileAsync(en);

        Assert.True(fake.Requests.Count > 4);
        var expected = Encoding.UTF8.GetBytes(TranslationPrompts.SystemPrompt);
        Assert.All(fake.Requests, q => Assert.Equal(expected, Encoding.UTF8.GetBytes(q.SystemPrompt)));
        Assert.DoesNotContain("Movie", TranslationPrompts.SystemPrompt);
    }

    [Fact]
    public async Task Glossary_IsReusedIfExists_AndRegeneratedOnRequest()
    {
        var en = WriteEnglish(5);
        var glossaryPath = Path.Combine(_dir, "Movie.glossary.json");
        File.WriteAllText(glossaryPath, """{"terms":[{"en":"Penelope","es":"Penélope","note":""}],"address":[]}""");

        var fake = new FakeTranslationClient();
        await Translator(fake).TranslateFileAsync(en);
        Assert.Empty(fake.GlossaryRequests);
        Assert.Contains("Penélope", fake.Requests[0].UserMessage);

        var fake2 = new FakeTranslationClient();
        await Translator(fake2, new TranslationOptions { Force = true, RegenerateGlossary = true }).TranslateFileAsync(en);
        Assert.Single(fake2.GlossaryRequests);
        Assert.DoesNotContain("Penélope", File.ReadAllText(glossaryPath));
        Assert.Contains("Line 3", fake2.GlossaryRequests[0].UserMessage);
        Assert.DoesNotContain("-->", fake2.GlossaryRequests[0].UserMessage); // sin tiempos
    }

    [Fact]
    public async Task UserMessage_NeverContainsTimings()
    {
        var en = WriteEnglish(30);
        var fake = new FakeTranslationClient();

        await Translator(fake).TranslateFileAsync(en);

        Assert.All(fake.Requests, q => Assert.DoesNotContain("-->", q.UserMessage));
        Assert.All(fake.Requests, q => Assert.DoesNotContain("00:00:", q.UserMessage));
    }

    // ------------------------------------------------------------ salida y copia

    [Fact]
    public async Task Sdh_ProducesEsSdhSrt()
    {
        var en = WriteEnglish(5, "Movie.en.sdh.srt");

        var r = await Translator(new FakeTranslationClient()).TranslateFileAsync(en);

        Assert.Equal(Path.Combine(_dir, "Movie.es.sdh.srt"), r.OutputPath);
        Assert.True(File.Exists(r.OutputPath));
    }

    [Fact]
    public async Task PlainCopy_CreatedOnlyIfNotExists()
    {
        var en = WriteEnglish(5);
        var plain = Path.Combine(_dir, "Movie.srt");

        var r = await Translator(new FakeTranslationClient()).TranslateFileAsync(en);

        Assert.Equal(PlainCopyStatus.Created, r.PlainCopy);
        Assert.Equal(File.ReadAllBytes(r.OutputPath), File.ReadAllBytes(plain));
    }

    [Fact]
    public async Task PlainCopy_ExistingUntaggedSrt_IsNotTouched()
    {
        var en = WriteEnglish(5);
        var plain = Path.Combine(_dir, "Movie.srt");
        File.WriteAllText(plain, "del usuario");

        var r = await Translator(new FakeTranslationClient()).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.Completed, r.Status);
        Assert.Equal(PlainCopyStatus.SkippedExists, r.PlainCopy);
        Assert.Equal("del usuario", File.ReadAllText(plain));
    }

    [Fact]
    public async Task NoPlainCopy_Option()
    {
        var en = WriteEnglish(5);

        var r = await Translator(new FakeTranslationClient(), new TranslationOptions { NoPlainCopy = true }).TranslateFileAsync(en);

        Assert.Equal(PlainCopyStatus.Disabled, r.PlainCopy);
        Assert.False(File.Exists(Path.Combine(_dir, "Movie.srt")));
        Assert.True(File.Exists(r.OutputPath));
    }

    [Fact]
    public async Task ExistingSpanish_NotOverwrittenWithoutForce()
    {
        var en = WriteEnglish(5);
        var es = Path.Combine(_dir, "Movie.es.srt");
        File.WriteAllText(es, "previo");
        var fake = new FakeTranslationClient();

        var r = await Translator(fake).TranslateFileAsync(en);

        Assert.Equal(TranslationStatus.SkippedExists, r.Status);
        Assert.Equal("previo", File.ReadAllText(es));
        Assert.Empty(fake.Requests);
        Assert.Empty(fake.GlossaryRequests);

        var r2 = await Translator(fake, new TranslationOptions { Force = true }).TranslateFileAsync(en);
        Assert.Equal(TranslationStatus.Completed, r2.Status);
        Assert.NotEqual("previo", File.ReadAllText(es));
    }

    [Fact]
    public async Task Output_IsUtf8NoBom_Crlf()
    {
        var en = WriteEnglish(3);

        var r = await Translator(new FakeTranslationClient()).TranslateFileAsync(en);

        var bytes = File.ReadAllBytes(r.OutputPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    // ------------------------------------------------------------ integración

    [Fact]
    public async Task EndToEnd_400Blocks_WithLogAndProgress()
    {
        var en = WriteEnglish(400);
        var fake = new FakeTranslationClient
        {
            // Mezcla de problemas: ids omitidos, JSON inválido y max_tokens; todo debe recuperarse.
            Handler = (req, call) => call switch
            {
                0 => FakeTranslationClient.Ok(req.Items.Where(i => i.Id % 17 != 0)),
                2 => new ChunkResponse { Items = null },
                4 => new ChunkResponse { StopReason = StopReasons.MaxTokens },
                _ => null,
            },
        };
        var reports = new List<TranslationProgress>();
        var log = new FileTranslationLog(Path.Combine(_dir, "_logs"), "Movie.en");

        var r = await Translator(fake).TranslateFileAsync(en, new SyncProgress(reports.Add), log);

        Assert.Equal(TranslationStatus.Completed, r.Status);
        Assert.Equal(400, r.BlockCount);
        var original = SrtParser.ParseFile(en);
        var translated = SrtParser.ParseFile(r.OutputPath);
        var report = SrtValidator.Validate(original, translated);
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(400, translated.Blocks.Count);
        Assert.True(File.Exists(Path.Combine(_dir, "Movie.srt")));
        Assert.Equal(400, reports[^1].BlocksDone);
        Assert.True(reports[^1].Usage.InputTokens > 0);
        Assert.True(reports[^1].EstimatedCostUsd > 0);
        var logText = File.ReadAllText(log.Path);
        Assert.Contains("stop_reason=max_tokens", logText);
        Assert.Contains("Completado", logText);
        Assert.False(Directory.Exists(new JobStore(_jobs, en).Directory));
    }

    private sealed class SyncProgress(Action<TranslationProgress> action) : IProgress<TranslationProgress>
    {
        public void Report(TranslationProgress value) => action(value);
    }

    // ------------------------------------------------------------ estimación

    [Fact]
    public async Task Estimate_UsesCountTokens_WithoutTranslating()
    {
        var en = WriteEnglish(300);
        var fake = new FakeTranslationClient();

        var e = await Translator(fake).EstimateAsync(en);

        Assert.Empty(fake.Requests);
        Assert.Empty(fake.GlossaryRequests);
        Assert.Equal(2, e.ChunkCount);
        Assert.True(e.GlossaryInputTokens > 0);
        Assert.True(e.TotalCostUsd > 0);
        Assert.False(File.Exists(Path.Combine(_dir, "Movie.glossary.json")));
    }
}
