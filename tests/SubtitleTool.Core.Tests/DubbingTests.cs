using System.Text;
using System.Text.Json.Nodes;
using SubtitleTool.Core.Dubbing;
using SubtitleTool.Core.Processes;
using SubtitleTool.Core.Srt;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Tests;

/// <summary>Doblaje: proyecto, etiquetado con Claude (personaje y emoción), plan para el motor y motores.</summary>
public sealed class DubbingTests : IDisposable
{
    private readonly string _dir;

    public DubbingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "subtool-dub-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string WriteSrt(string name, params string[] texts)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < texts.Length; i++)
            sb.Append($"{i + 1}\n00:00:{i * 3:00},000 --> 00:00:{i * 3 + 2:00},000\n{texts[i]}\n\n");
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    // ---------- Etiquetas ----------

    [Theory]
    [InlineData("Odiseo | m | ira | 0.8", "Odiseo", "m", "ira", 0.8)]
    [InlineData("Penélope|f|Sorpresa|0,5", "Penélope", "f", "sorpresa", 0.5)]
    [InlineData("Soldado 1 | hombre | enfado | 2", "Soldado 1", "m", "ira", 1.0)]
    [InlineData("- | ? | neutral | 0.3", "", "?", "neutral", 0.3)]
    [InlineData("Narrador | ? | algo raro", "Narrador", "?", "neutral", 0.6)]
    public void Parse_ReadsSpeakerGenderEmotionAndIntensity(string text, string speaker, string gender, string emotion, double intensity)
    {
        var a = DubbingAnnotator.Parse(text);
        Assert.NotNull(a);
        Assert.Equal(speaker, a!.Speaker);
        Assert.Equal(gender, a.Gender);
        Assert.Equal(emotion, a.Emotion);
        Assert.Equal(intensity, a.Intensity, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Odiseo")]
    [InlineData("Odiseo | m")]
    public void Parse_RejectsIncompleteAnswers(string text) => Assert.Null(DubbingAnnotator.Parse(text));

    [Theory]
    [InlineData("Alegría", "alegria")]
    [InlineData("MELANCOLÍA", "melancolia")]
    [InlineData("angry", "ira")]
    [InlineData("pánico", "miedo")]
    [InlineData("xyz", "neutral")]
    public void Emotions_Normalize(string input, string expected) => Assert.Equal(expected, DubbingEmotions.Normalize(input));

    // ---------- Proyecto ----------

    [Fact]
    public void Project_FromSrt_SkipsEmptyBlocks_AndKeepsBlockIds()
    {
        var path = WriteSrt("Movie.es-cu.srt", "Hola, asere", " ", "¿Qué volá?");
        var p = DubbingProject.FromSrt(path, SrtParser.ParseFile(path));

        Assert.Equal([1, 3], p.Lines.Select(l => l.Id));
        Assert.Equal(6, p.Lines[1].Start);
        Assert.Equal(Path.Combine(_dir, "Movie.es-cu.doblaje.json"), DubbingProject.PathFor(path));
    }

    [Fact]
    public void Project_SyncCharacters_AssignsVoiceByGender_AndVariesPitch()
    {
        var p = new DubbingProject
        {
            Lines =
            [
                new() { Id = 1, Speaker = "Odiseo" }, new() { Id = 2, Speaker = "Odiseo" },
                new() { Id = 3, Speaker = "Penélope" }, new() { Id = 4, Speaker = "Telémaco" },
            ],
        };
        var added = p.SyncCharacters(new Dictionary<string, string> { ["Odiseo"] = "m", ["Penélope"] = "f", ["Telémaco"] = "m" });

        Assert.Equal(3, added);
        Assert.Equal("Odiseo", p.Characters[0].Name); // el que más habla primero
        Assert.Equal(CubanVoices.Female, p.Character("Penélope")!.EdgeVoice);
        Assert.Equal(CubanVoices.Male, p.Character("telémaco")!.EdgeVoice);
        Assert.NotEqual(p.Character("Odiseo")!.Pitch, p.Character("Telémaco")!.Pitch);

        // Un personaje sin líneas desaparece; los demás conservan su voz.
        p.Lines[3].Speaker = "Odiseo";
        p.Character("Penélope")!.SamplePath = "x.ogg";
        p.SyncCharacters();
        Assert.Equal(["Odiseo", "Penélope"], p.Characters.Select(c => c.Name));
        Assert.Equal("x.ogg", p.Character("Penélope")!.SamplePath);
    }

    [Fact]
    public void Store_KeepsAnnotations_AndTakesTextFromTheSrt()
    {
        var path = WriteSrt("Movie.es-cu.srt", "Hola", "Adiós");
        var p = DubbingProjectStore.LoadOrCreate(path);
        p.Lines[0].Speaker = "Odiseo";
        p.Lines[0].Emotion = "ira";
        p.Lines[0].Intensity = 0.9;
        p.Annotated = true;
        p.SyncCharacters(new Dictionary<string, string> { ["Odiseo"] = "m" });
        p.Characters[0].Rate = 30;
        DubbingProjectStore.Save(p);

        // El subtítulo se corrige después: manda el texto nuevo, se conservan personaje y emoción.
        WriteSrt("Movie.es-cu.srt", "Hola, asere", "Adiós");
        var again = DubbingProjectStore.LoadOrCreate(path);

        Assert.True(again.Annotated);
        Assert.Equal("Hola, asere", again.Lines[0].Text);
        Assert.Equal("Odiseo", again.Lines[0].Speaker);
        Assert.Equal("ira", again.Lines[0].Emotion);
        Assert.Equal(0.9, again.Lines[0].Intensity, 3);
        Assert.Equal(30, again.Character("Odiseo")!.Rate);
    }

    [Fact]
    public void Store_BlocksMergedInTheSrt_DoNotInheritShiftedLabels()
    {
        var path = WriteSrt("Movie.es-cu.srt", "Uno", "Dos", "Tres");
        var p = DubbingProjectStore.LoadOrCreate(path);
        p.Lines[0].Speaker = "A";
        p.Lines[1].Speaker = "B";
        p.Lines[2].Speaker = "C";
        p.SyncCharacters();
        DubbingProjectStore.Save(p);

        // Se fusionan los bloques 1 y 2: el antiguo 3 pasa a ser el 2.
        WriteSrt("Movie.es-cu.srt", "Uno. Dos", "Tres");
        var again = DubbingProjectStore.LoadOrCreate(path);

        Assert.Equal("", again.Lines[0].Speaker); // texto distinto y otra forma: sin etiqueta
        Assert.Equal("", again.Lines[1].Speaker); // «Tres» ahora es el id 2, que antes era «Dos» de B
        Assert.DoesNotContain(again.Characters, c => c.Name == "B");
    }

    [Theory]
    [InlineData("{ no es json")]
    [InlineData("""{"lines": null, "characters": null}""")]
    [InlineData("""{"lines": [{"id": 1, "speaker": null}, {"id": 1}], "characters": null}""")]
    public void Store_DamagedProjectFile_StartsFresh(string json)
    {
        var path = WriteSrt("Movie.es-cu.srt", "Hola");
        File.WriteAllText(DubbingProject.PathFor(path), json);
        var p = DubbingProjectStore.LoadOrCreate(path);
        Assert.Single(p.Lines);
        Assert.Equal("", p.Lines[0].Speaker);
    }

    [Theory]
    [InlineData(@"D:\Pelis\Odisea.doblaje-cu.mkv", true)]
    [InlineData(@"D:\Pelis\Odisea.doblaje-cu.prueba-000100-000300.mkv", true)]
    [InlineData(@"D:\Pelis\Odisea.doblaje-cu.mkv.tmp.mkv", true)]
    [InlineData(@"D:\Pelis\Odisea.mkv", false)]
    public void GeneratedOutputs_AreRecognized(string path, bool expected) =>
        Assert.Equal(expected, DubbingRunner.IsGeneratedOutput(path));

    [Fact]
    public void LibraryScan_SkipsDubbedOutputs()
    {
        File.WriteAllText(Path.Combine(_dir, "Odisea.mkv"), "");
        File.WriteAllText(Path.Combine(_dir, "Odisea.doblaje-cu.mkv"), "");
        File.WriteAllText(Path.Combine(_dir, "Odisea.doblaje-cu.prueba-000100-000300.mkv"), "");
        var found = SubtitleTool.Core.Library.LibraryScanner.FindVideos(_dir);
        Assert.Equal(["Odisea.mkv"], found.Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(" 45%|####      | 1.2G/2.7G [00:10<00:12, 120MB/s]", false)]
    [InlineData("Downloading torch (2.3GiB)", false)]
    [InlineData(" + edge-tts==7.3.1", true)]
    [InlineData("   ", false)]
    public void InstallLog_SkipsProgressBars(string line, bool expected) =>
        Assert.Equal(expected, DubbingEngines.IsWorthLogging(line));

    [Fact]
    public void Store_DamagedProjectFile_LegacyCase()
    {
        var path = WriteSrt("Movie.es-cu.srt", "Hola");
        File.WriteAllText(DubbingProject.PathFor(path), "{ no es json");
        var p = DubbingProjectStore.LoadOrCreate(path);
        Assert.Single(p.Lines);
        Assert.False(p.Annotated);
    }

    // ---------- Etiquetado con Claude ----------

    private static DubbingProject ProjectWith(int lines) => new()
    {
        Lines = Enumerable.Range(1, lines).Select(i => new DubbingLine { Id = i, Text = $"Línea {i}" }).ToList(),
    };

    [Fact]
    public async Task Annotate_FillsLines_CreatesCharacters_AndUsesMajorityGender()
    {
        var fake = new FakeTranslationClient
        {
            Handler = (req, _) => new ChunkResponse
            {
                Items = req.Items.Select(i => new TranslationItem(i.Id, i.Id switch
                {
                    1 => "Odiseo | m | ira | 0.9",
                    2 => "Penélope | f | sorpresa | 0.5",
                    3 => "Odiseo | ? | calma | 0.4",
                    _ => "Odiseo | m | neutral | 0.3",
                })).ToList(),
                StopReason = StopReasons.EndTurn,
                Usage = new TokenUsage(10, 5, 0, 0),
            },
        };
        var p = ProjectWith(4);
        var usage = await new DubbingAnnotator(fake).AnnotateAsync(p, null, null, CancellationToken.None);

        Assert.True(p.Annotated);
        Assert.Equal(["Odiseo", "Penélope", "Odiseo", "Odiseo"], p.Lines.Select(l => l.Speaker));
        Assert.Equal("ira", p.Lines[0].Emotion);
        Assert.Equal(0.9, p.Lines[0].Intensity, 3);
        Assert.Equal("m", p.Character("Odiseo")!.Gender);
        Assert.Equal(CubanVoices.Female, p.Character("Penélope")!.EdgeVoice);
        Assert.Equal(10, usage.InputTokens);
        Assert.Contains("personaje | sexo | emoción | intensidad", fake.Requests[0].SystemPrompt);
    }

    [Fact]
    public async Task Annotate_RetriesMissingIds_AndSendsKnownCharactersToLaterChunks()
    {
        var fake = new FakeTranslationClient
        {
            // Primera llamada: omite la línea 2. Las siguientes responden todo.
            Handler = (req, n) => new ChunkResponse
            {
                Items = req.Items.Where(i => n > 0 || i.Id != 2).Select(i => new TranslationItem(i.Id, $"Personaje{i.Id % 2} | m | neutral | 0.5")).ToList(),
                StopReason = StopReasons.EndTurn,
            },
        };
        var p = ProjectWith(14);
        await new DubbingAnnotator(fake, chunkSize: 10).AnnotateAsync(p, null, null, CancellationToken.None);

        Assert.All(p.Lines, l => Assert.StartsWith("Personaje", l.Speaker));
        Assert.Equal([2], fake.Requests[1].Items.Select(i => i.Id)); // reintento solo de la que faltó
        Assert.Contains("PERSONAJES YA IDENTIFICADOS", fake.Requests[2].UserMessage);
        Assert.Contains("CONTEXTO PREVIO", fake.Requests[2].UserMessage);
    }

    [Fact]
    public async Task Annotate_Refusal_Throws()
    {
        var fake = new FakeTranslationClient
        {
            Handler = (_, _) => new ChunkResponse { StopReason = StopReasons.Refusal, RefusalExplanation = "no" },
        };
        await Assert.ThrowsAsync<AnnotationException>(() =>
            new DubbingAnnotator(fake).AnnotateAsync(ProjectWith(3), null, null, CancellationToken.None));
    }

    [Fact]
    public void UserMessage_IncludesGlossaryNamesWithNotes()
    {
        var g = new Glossary { Terms = [new GlossaryTerm { En = "Penelope", Es = "Penélope", Note = "esposa de Odiseo" }] };
        var msg = DubbingAnnotator.BuildUserMessage(g, [], [], [new DubbingLine { Id = 1, Text = "Hola" }]);
        Assert.Contains("Penélope (esposa de Odiseo)", msg);
        Assert.Contains("\"id\":1", msg.Replace(" ", ""));
    }

    // ---------- Plan para el motor ----------

    private DubbingRunner Runner(out DubbingEngines engines)
    {
        engines = new DubbingEngines(Path.Combine(_dir, "voces"));
        return new DubbingRunner(engines, Path.Combine(_dir, "scripts"), workRoot: Path.Combine(_dir, "work"));
    }

    [Fact]
    public void OutputPath_FullMovie_AndFragment()
    {
        var video = Path.Combine(_dir, "La Odisea.mkv");
        Assert.Equal(Path.Combine(_dir, "La Odisea.doblaje-cu.mkv"), DubbingRunner.OutputPathFor(video, null));
        Assert.Equal(Path.Combine(_dir, "La Odisea.doblaje-cu.prueba-012430-012634.mkv"),
            DubbingRunner.OutputPathFor(video, (new TimeSpan(1, 24, 30), new TimeSpan(1, 26, 34))));
    }

    [Fact]
    public void BuildPlan_EmotionsScaled_VoicesAndOptionalEngines()
    {
        var sample = Path.Combine(_dir, "odiseo.ogg");
        File.WriteAllText(sample, "audio");
        var project = new DubbingProject
        {
            SubtitlePath = Path.Combine(_dir, "Movie.es-cu.srt"),
            Lines =
            [
                new() { Id = 1, Start = 1, End = 2, Text = "Hola", Speaker = "Odiseo", Emotion = "ira", Intensity = 0.8 },
                new() { Id = 2, Start = 3, End = 4, Text = "Adiós", Speaker = "PENÉLOPE", Emotion = DubbingEmotions.Neutral },
            ],
            Characters =
            [
                new() { Name = "Odiseo", EdgeVoice = CubanVoices.Male, Rate = 20, Pitch = -12, SamplePath = sample },
                new() { Name = "Penélope", EdgeVoice = CubanVoices.Female, Rate = -5, SamplePath = Path.Combine(_dir, "no-existe.ogg") },
            ],
        };
        var runner = Runner(out var engines);
        var request = new DubbingRequest
        {
            VideoPath = Path.Combine(_dir, "Movie.mkv"),
            Project = project,
            VoiceEngine = DubbingVoiceEngine.Emotions,
            UseOwnVoices = true,
            EmotionStrength = 1.5,
            Range = (TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(60)),
        };

        var plan = runner.BuildPlan(request, "ffmpeg.exe", "ffprobe.exe");

        Assert.Equal("indextts", (string)plan["engine"]!);
        Assert.Equal(1.0, (double)plan["lines"]![0]!["emotion_vector"]!["ira"]!, 3); // 0.8 × 1.5, con tope 1
        Assert.Empty(plan["lines"]![1]!["emotion_vector"]!.AsObject());
        Assert.Equal("Penélope", (string)plan["lines"]![1]!["speaker"]!); // la voz se busca con la grafía de la lista
        Assert.Equal("Cubano", (string)plan["subtitle_title"]!);
        var odiseo = plan["voices"]!["Odiseo"]!;
        Assert.Equal("+20%", (string)odiseo["rate"]!);
        Assert.Equal("-12Hz", (string)odiseo["pitch"]!);
        Assert.Equal(sample, (string)odiseo["sample"]!);
        Assert.Null(plan["voices"]!["Penélope"]!["sample"]); // la muestra no existe: sin voz propia
        Assert.Equal("-5%", (string)plan["voices"]!["Penélope"]!["rate"]!);
        Assert.Equal(60.0, (double)plan["range"]!["end"]!);
        Assert.NotNull(plan["seedvc"]);
        Assert.Equal(engines.Python(DubbingEngine.IndexTts), (string)plan["indextts"]!["python"]!);
        Assert.EndsWith("Movie.doblaje-cu.prueba-000000-000100.mkv", (string)plan["output"]!);
        Assert.Equal(engines.HfCache, (string)plan["env"]!["HF_HUB_CACHE"]!);

        // Sin voces propias ni emociones: ni Seed-VC ni IndexTTS en el plan.
        var simple = runner.BuildPlan(request with { UseOwnVoices = false, VoiceEngine = DubbingVoiceEngine.Edge, Range = null },
            "ffmpeg.exe", "ffprobe.exe");
        Assert.Equal("edge", (string)simple["engine"]!);
        Assert.Null(simple["seedvc"]);
        Assert.Null(simple["indextts"]);
        Assert.Null(simple["range"]);
        Assert.Null(simple["voices"]!["Odiseo"]!["sample"]);
    }

    [Fact]
    public void MissingEngines_DependsOnOptions()
    {
        var sample = Path.Combine(_dir, "a.ogg");
        File.WriteAllText(sample, "x");
        var runner = Runner(out _);
        var project = new DubbingProject { Characters = [new() { Name = "A", SamplePath = sample }] };
        var basic = new DubbingRequest { VideoPath = "v.mkv", Project = project };

        Assert.Equal(["Voces cubanas (edge-tts)"], runner.MissingEngines(basic));
        Assert.Equal(3, runner.MissingEngines(basic with { VoiceEngine = DubbingVoiceEngine.Emotions, UseOwnVoices = true }).Count);
    }

    [Fact]
    public async Task Run_WithoutEngines_FailsBeforeStarting()
    {
        var runner = Runner(out _);
        var request = new DubbingRequest { VideoPath = Path.Combine(_dir, "v.mkv"), Project = new DubbingProject() };
        var ex = await Assert.ThrowsAsync<DubbingException>(() =>
            runner.RunAsync(request, "ffmpeg", "ffprobe", new Progress<DubbingProgress>(), CancellationToken.None));
        Assert.Contains("edge-tts", ex.Message);
    }

    [Fact]
    public async Task Run_ParsesProgressAndResultEvents()
    {
        var engines = new DubbingEngines(Path.Combine(_dir, "voces"));
        MarkInstalled(engines, DubbingEngine.Base, "edge-tts " + DubbingEngines.EdgeTtsVersion);
        var fake = new ScriptedRunner(
        [
            """{"fase": "voces", "hecho": 1, "total": 2, "mensaje": "Voz de la línea 1"}""",
            "ruido que no es un evento",
            """{"fase": "fin", "salida": "C:/x/Movie.doblaje-cu.mkv", "informe": {"lineas": 2, "aceleradas": 1, "desbordan": 0, "retraso_max": 0.2}}""",
        ]);
        var runner = new DubbingRunner(engines, Path.Combine(_dir, "scripts"), fake, Path.Combine(_dir, "work"));
        var events = new List<DubbingProgress>();
        var request = new DubbingRequest { VideoPath = Path.Combine(_dir, "Movie.mkv"), Project = new DubbingProject() };

        var result = await runner.RunAsync(request, "ffmpeg", "ffprobe", new SyncProgress<DubbingProgress>(events.Add), CancellationToken.None);

        Assert.Equal("C:/x/Movie.doblaje-cu.mkv", result.OutputPath);
        Assert.Equal(1, result.SpedUp);
        Assert.Single(events);
        Assert.Equal("Generando voces", events[0].PhaseLabel);
        Assert.EndsWith("motor_doblaje.py", fake.Arguments[1]);
        Assert.True(File.Exists(fake.Arguments[2])); // plan.json escrito
    }

    [Fact]
    public async Task Run_EngineErrorEvent_BecomesDubbingException()
    {
        var engines = new DubbingEngines(Path.Combine(_dir, "voces"));
        MarkInstalled(engines, DubbingEngine.Base, "edge-tts " + DubbingEngines.EdgeTtsVersion);
        var fake = new ScriptedRunner(["""{"fase": "error", "mensaje": "No hay líneas con texto en ese tramo."}"""], exitCode: 1);
        var runner = new DubbingRunner(engines, Path.Combine(_dir, "scripts"), fake, Path.Combine(_dir, "work"));
        var request = new DubbingRequest { VideoPath = Path.Combine(_dir, "Movie.mkv"), Project = new DubbingProject() };

        var ex = await Assert.ThrowsAsync<DubbingException>(() =>
            runner.RunAsync(request, "ffmpeg", "ffprobe", new Progress<DubbingProgress>(), CancellationToken.None));
        Assert.Contains("No hay líneas con texto", ex.Message);
    }

    [Fact]
    public void Engines_InstalledOnlyWithMatchingMarker()
    {
        var engines = new DubbingEngines(Path.Combine(_dir, "voces"));
        Assert.False(engines.IsInstalled(DubbingEngine.Base));
        MarkInstalled(engines, DubbingEngine.Base, "edge-tts 0.0.1"); // versión vieja
        Assert.False(engines.IsInstalled(DubbingEngine.Base));
        MarkInstalled(engines, DubbingEngine.Base, "edge-tts " + DubbingEngines.EdgeTtsVersion);
        Assert.True(engines.IsInstalled(DubbingEngine.Base));
        Assert.EndsWith(Path.Combine("seed-vc", ".venv", "Scripts", "python.exe"), engines.Python(DubbingEngine.SeedVc));
    }

    [Fact]
    public async Task Install_AddOnBeforeBase_IsRejected()
    {
        var engines = new DubbingEngines(Path.Combine(_dir, "voces"));
        await Assert.ThrowsAsync<DubbingInstallException>(() =>
            engines.InstallAsync(DubbingEngine.SeedVc, "ffmpeg", _dir, new Progress<string>(), CancellationToken.None));
    }

    private static void MarkInstalled(DubbingEngines engines, DubbingEngine engine, string version)
    {
        var py = engines.Python(engine);
        Directory.CreateDirectory(Path.GetDirectoryName(py)!);
        File.WriteAllText(py, "");
        File.WriteAllText(Path.Combine(engines.Folder(engine), ".instalado"), version);
    }

    /// <summary>IProgress síncrono (Progress&lt;T&gt; publica en el pool y la prueba podría leer antes).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Proceso falso: emite líneas de stdout por el callback y devuelve el código indicado.</summary>
    private sealed class ScriptedRunner(IReadOnlyList<string> stdout, int exitCode = 0) : IProcessRunner
    {
        public List<string> Arguments { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken = default) =>
            RunAsync(fileName, arguments, new ProcessRunOptions(), cancellationToken);

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, ProcessRunOptions options, CancellationToken cancellationToken = default)
        {
            Arguments.AddRange(arguments);
            foreach (var line in stdout)
                options.OnStandardOutputLine?.Invoke(line);
            return Task.FromResult(new ProcessResult(exitCode, string.Join("\n", stdout), ""));
        }
    }
}

/// <summary>Lectura línea a línea de la salida de un proceso real.</summary>
public sealed class ProcessRunnerStreamingTests
{
    [Fact]
    public async Task OnStandardOutputLine_ReceivesEachLine_AndFullOutputIsStillReturned()
    {
        var lines = new List<string>();
        var r = await new ProcessRunner().RunAsync("cmd.exe", ["/c", "echo uno& echo dos& echo tres 1>&2"],
            new ProcessRunOptions
            {
                OnStandardOutputLine = l => { lock (lines) lines.Add(l.Trim()); },
                EnvironmentVariables = new Dictionary<string, string> { ["SUBTOOL_PRUEBA"] = "1" },
            });

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["uno", "dos"], lines);
        Assert.Contains("dos", r.StandardOutput);
        Assert.Contains("tres", r.StandardError);
    }
}
