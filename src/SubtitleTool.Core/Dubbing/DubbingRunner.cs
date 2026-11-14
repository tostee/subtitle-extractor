using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SubtitleTool.Core.Processes;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Dubbing;

/// <summary>Qué voz usar para generar las líneas.</summary>
public enum DubbingVoiceEngine
{
    /// <summary>edge-tts: rápido, sin emociones.</summary>
    Edge,
    /// <summary>IndexTTS-2.5: con la emoción de cada línea.</summary>
    Emotions,
}

/// <summary>Petición de doblaje.</summary>
public sealed record DubbingRequest
{
    public required string VideoPath { get; init; }
    public required DubbingProject Project { get; init; }
    public DubbingVoiceEngine VoiceEngine { get; init; } = DubbingVoiceEngine.Edge;
    /// <summary>Aplicar las muestras de voz de los personajes (Seed-VC).</summary>
    public bool UseOwnVoices { get; init; }
    /// <summary>Solo un fragmento (prueba rápida); null = la película entera.</summary>
    public (TimeSpan Start, TimeSpan End)? Range { get; init; }
    /// <summary>Intensidad global de las emociones (multiplica la de cada línea).</summary>
    public double EmotionStrength { get; init; } = 1.0;
    /// <summary>Pista de audio original que se usa de fondo (índice entre las pistas de audio).</summary>
    public int AudioIndex { get; init; }
}

/// <summary>Progreso del doblaje: fase ("voces", "voces_propias", "tiempos", "mezcla"), avance y mensaje.</summary>
public sealed record DubbingProgress(string Phase, int Done, int Total, string Message)
{
    public string PhaseLabel => Phase switch
    {
        "voces" => "Generando voces",
        "voces_propias" => "Aplicando voces propias",
        "tiempos" => "Ajustando tiempos",
        "mezcla" => "Creando el video",
        _ => "Preparando",
    };
}

/// <summary>Resultado: video creado y resumen de la sincronización.</summary>
public sealed record DubbingResult(string OutputPath, int Lines, int SpedUp, int Overflowing, double MaxDelaySeconds);

public sealed class DubbingException : Exception
{
    public DubbingException(string message) : base(message) { }
}

/// <summary>Lanza el motor de doblaje (motor_doblaje.py con el Python del motor base) y sigue su progreso.</summary>
public sealed class DubbingRunner
{
    private readonly DubbingEngines _engines;
    private readonly IProcessRunner _runner;
    private readonly string _scriptsDir;
    private readonly string _workRoot;

    /// <param name="scriptsDir">Carpeta con motor_doblaje.py, lote_seedvc.py y lote_indextts.py.</param>
    /// <param name="workRoot">Carpeta de los audios intermedios (por defecto <c>%LOCALAPPDATA%\SubtitleTool\doblaje</c>).</param>
    public DubbingRunner(DubbingEngines engines, string scriptsDir, IProcessRunner? runner = null, string? workRoot = null)
    {
        _engines = engines;
        _scriptsDir = scriptsDir;
        _runner = runner ?? new ProcessRunner();
        _workRoot = workRoot ?? Path.Combine(AppPaths.Root, "doblaje");
    }

    /// <summary>Video de salida: <c>Película.doblaje-cu.mkv</c>, o con el tramo para un fragmento de prueba.</summary>
    public static string OutputPathFor(string videoPath, (TimeSpan Start, TimeSpan End)? range)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;
        var name = Path.GetFileNameWithoutExtension(videoPath);
        var suffix = range is { } r ? $".prueba-{Stamp(r.Start)}-{Stamp(r.End)}" : "";
        return Path.Combine(dir, $"{name}.doblaje-cu{suffix}.mkv");
    }

    /// <summary>Videos que crea el doblaje (o su temporal): no son películas nuevas para extraer y traducir.</summary>
    public static bool IsGeneratedOutput(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains(".doblaje-cu.", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp.mkv", StringComparison.OrdinalIgnoreCase);
    }

    private static string Stamp(TimeSpan t) => $"{(int)t.TotalHours:00}{t.Minutes:00}{t.Seconds:00}";

    /// <summary>Carpeta de trabajo por video (los audios generados se reutilizan al repetir o retomar).</summary>
    public string WorkDirFor(string videoPath)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(videoPath).ToLowerInvariant())))[..12];
        // Nombre recortado: los nombres de release son largos y las rutas de los audios intermedios rozarían MAX_PATH.
        var name = Path.GetFileNameWithoutExtension(videoPath);
        return Path.Combine(_workRoot, $"{(name.Length > 40 ? name[..40].TrimEnd() : name)}-{hash}");
    }

    /// <summary>Plan JSON que lee el motor.</summary>
    public JsonObject BuildPlan(DubbingRequest request, string ffmpeg, string ffprobe)
    {
        var p = request.Project;
        var lines = new JsonArray();
        foreach (var l in p.Lines)
        {
            var emo = new JsonObject();
            if (l.Emotion != DubbingEmotions.Neutral && DubbingEmotions.Vector.Contains(l.Emotion))
                emo[l.Emotion] = Math.Round(Math.Clamp(l.Intensity * request.EmotionStrength, 0, 1), 2);
            lines.Add(new JsonObject
            {
                ["id"] = l.Id,
                ["start"] = l.Start,
                ["end"] = l.End,
                ["text"] = l.Text,
                // Grafía del personaje tal cual está en la lista (en C# no se distinguen mayúsculas; en el motor sí).
                ["speaker"] = p.Character(l.Speaker)?.Name ?? "",
                ["emotion_vector"] = emo,
            });
        }
        var voices = new JsonObject();
        foreach (var c in p.Characters)
            voices[c.Name] = Voice(c, request.UseOwnVoices);
        var plan = new JsonObject
        {
            ["video"] = Path.GetFullPath(request.VideoPath),
            ["ffmpeg"] = ffmpeg,
            ["ffprobe"] = ffprobe,
            ["work_dir"] = WorkDirFor(request.VideoPath),
            ["output"] = OutputPathFor(request.VideoPath, request.Range),
            ["engine"] = request.VoiceEngine == DubbingVoiceEngine.Emotions ? "indextts" : "edge",
            ["audio_index"] = request.AudioIndex,
            ["subtitles"] = p.SubtitlePath,
            ["subtitle_title"] = p.SubtitlePath.Contains(".es-cu", StringComparison.OrdinalIgnoreCase) ? "Cubano" : "Español",
            ["lines"] = lines,
            ["voices"] = voices,
            ["default_voice"] = Voice(new DubbingCharacter { Name = "", EdgeVoice = CubanVoices.Male, Rate = 15 }, false),
            ["env"] = ToJson(_engines.Environment),
        };
        if (request.Range is { } r)
            plan["range"] = new JsonObject { ["start"] = r.Start.TotalSeconds, ["end"] = r.End.TotalSeconds };
        if (request.VoiceEngine == DubbingVoiceEngine.Emotions)
            plan["indextts"] = new JsonObject
            {
                ["python"] = _engines.Python(DubbingEngine.IndexTts),
                ["script"] = Path.Combine(_scriptsDir, "lote_indextts.py"),
                ["repo"] = _engines.Folder(DubbingEngine.IndexTts),
                ["alpha"] = 1.0,
            };
        if (request.UseOwnVoices && p.Characters.Any(c => HasSample(c)))
            plan["seedvc"] = new JsonObject
            {
                ["python"] = _engines.Python(DubbingEngine.SeedVc),
                ["script"] = Path.Combine(_scriptsDir, "lote_seedvc.py"),
                ["repo"] = _engines.Folder(DubbingEngine.SeedVc),
                ["steps"] = 30,
            };
        return plan;
    }

    private static bool HasSample(DubbingCharacter c) => !string.IsNullOrWhiteSpace(c.SamplePath) && File.Exists(c.SamplePath);

    private static JsonObject Voice(DubbingCharacter c, bool ownVoices)
    {
        var v = new JsonObject
        {
            ["edge_voice"] = c.EdgeVoice,
            ["rate"] = (c.Rate >= 0 ? "+" : "") + c.Rate.ToString(CultureInfo.InvariantCulture) + "%",
            ["pitch"] = (c.Pitch >= 0 ? "+" : "") + c.Pitch.ToString(CultureInfo.InvariantCulture) + "Hz",
        };
        if (ownVoices && HasSample(c))
        {
            var info = new FileInfo(c.SamplePath!);
            v["sample"] = info.FullName;
            v["sample_key"] = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
                $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..12];
        }
        return v;
    }

    private static JsonObject ToJson(IReadOnlyDictionary<string, string> env)
    {
        var o = new JsonObject();
        foreach (var (k, v) in env)
            o[k] = v;
        return o;
    }

    /// <summary>El video a medio crear (varios GB) no se queda junto a la película si se cancela o falla.</summary>
    private static void DeleteTemporaryOutput(DubbingRequest request)
    {
        try
        {
            File.Delete(OutputPathFor(request.VideoPath, request.Range) + ".tmp.mkv");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Puede seguir abierto un instante por ffmpeg: no es grave.
        }
    }

    /// <summary>Comprueba que estén instalados los motores que pide la petición.</summary>
    public IReadOnlyList<string> MissingEngines(DubbingRequest request)
    {
        var missing = new List<string>();
        if (!_engines.IsInstalled(DubbingEngine.Base))
            missing.Add(DubbingEngines.All[0].Name);
        if (request.VoiceEngine == DubbingVoiceEngine.Emotions && !_engines.IsInstalled(DubbingEngine.IndexTts))
            missing.Add(DubbingEngines.All[2].Name);
        if (request.UseOwnVoices && request.Project.Characters.Any(HasSample) && !_engines.IsInstalled(DubbingEngine.SeedVc))
            missing.Add(DubbingEngines.All[1].Name);
        return missing;
    }

    /// <exception cref="DubbingException">Falta un motor o el motor informó de un error.</exception>
    public async Task<DubbingResult> RunAsync(DubbingRequest request, string ffmpeg, string ffprobe, IProgress<DubbingProgress> progress,
        CancellationToken ct)
    {
        var missing = MissingEngines(request);
        if (missing.Count > 0)
            throw new DubbingException("Falta instalar: " + string.Join(", ", missing) + ".");
        var work = WorkDirFor(request.VideoPath);
        Directory.CreateDirectory(work);
        var planPath = Path.Combine(work, "plan.json");
        await File.WriteAllTextAsync(planPath, BuildPlan(request, ffmpeg, ffprobe).ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), ct).ConfigureAwait(false);

        DubbingResult? result = null;
        string? error = null;
        var stderrTail = new Queue<string>();
        var options = new ProcessRunOptions
        {
            WorkingDirectory = work,
            EnvironmentVariables = _engines.Environment,
            OnStandardOutputLine = line =>
            {
                if (!line.StartsWith('{'))
                    return;
                try
                {
                    var ev = JsonNode.Parse(line)!.AsObject();
                    var phase = (string?)ev["fase"] ?? "";
                    switch (phase)
                    {
                        case "fin":
                            var inf = ev["informe"]!;
                            result = new DubbingResult((string)ev["salida"]!, (int)inf["lineas"]!, (int)inf["aceleradas"]!,
                                (int)inf["desbordan"]!, (double)inf["retraso_max"]!);
                            break;
                        case "error":
                            error = (string?)ev["mensaje"];
                            break;
                        default:
                            progress.Report(new DubbingProgress(phase, (int?)ev["hecho"] ?? 0, (int?)ev["total"] ?? 0, (string?)ev["mensaje"] ?? ""));
                            break;
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NullReferenceException)
                {
                    // Línea que no es un evento: se ignora.
                }
            },
            OnStandardErrorLine = line =>
            {
                lock (stderrTail)
                {
                    stderrTail.Enqueue(line);
                    while (stderrTail.Count > 30)
                        stderrTail.Dequeue();
                }
            },
        };
        ProcessResult r;
        try
        {
            r = await _runner.RunAsync(_engines.Python(DubbingEngine.Base),
                ["-I", Path.Combine(_scriptsDir, "motor_doblaje.py"), planPath], options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DeleteTemporaryOutput(request);
            throw;
        }
        if (result is not null && r.ExitCode == 0)
            return result;
        DeleteTemporaryOutput(request);
        string detail;
        lock (stderrTail)
            detail = error ?? stderrTail.LastOrDefault(l => l.Trim().Length > 0) ?? $"código {r.ExitCode}";
        throw new DubbingException("El doblaje falló: " + detail);
    }
}
