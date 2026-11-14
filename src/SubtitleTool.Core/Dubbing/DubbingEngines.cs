using System.IO.Compression;
using System.Security.Cryptography;
using SubtitleTool.Core.Processes;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Dubbing;

public enum DubbingEngine
{
    /// <summary>edge-tts: voces cubanas de Microsoft (necesita internet). Es el motor que ejecuta el doblaje.</summary>
    Base,
    /// <summary>Seed-VC: cambia el timbre por el de una muestra de voz (voces propias). Necesita GPU NVIDIA.</summary>
    SeedVc,
    /// <summary>IndexTTS-2.5: voz con emociones. Necesita GPU NVIDIA.</summary>
    IndexTts,
}

public sealed class DubbingInstallException : Exception
{
    public DubbingInstallException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Descripción de un motor para la interfaz.</summary>
public sealed record DubbingEngineInfo(DubbingEngine Engine, string Name, string Description, string Size, bool NeedsGpu);

/// <summary>
/// Instala y localiza los motores de voz en una carpeta (por defecto <c>%LOCALAPPDATA%\SubtitleTool\voces</c>).
/// Todo queda dentro de esa carpeta: uv, Python, cachés y modelos. Versiones fijadas para que sea reproducible.
/// </summary>
public sealed class DubbingEngines
{
    public const string UvVersion = "0.11.8";
    public const string UvSha256 = "c84629a56e0706b69a47ea35862208af827cb6fbfa1d0ca763c52c67594637e8";
    public const string EdgeTtsVersion = "7.3.1";
    public const string SeedVcCommit = "51383efd921027683c89e5348211d93ff12ac2a8";
    public const string IndexTtsCommit = "d9e41aac89fd00b3d71497fddb287b7f24613712";
    public const string IndexTtsModel = "IndexTeam/IndexTTS-2.5";
    /// <summary>Versión fijada del modelo (el código de IndexTTS también lo está).</summary>
    public const string IndexTtsModelRevision = "c39ce5ba981572cb187443877ff559dfb246ce63";
    public const string TorchIndex = "https://download.pytorch.org/whl/cu128";

    /// <summary>Dependencias de Seed-VC para convertir voz (sin la interfaz web ni el entrenamiento). PyTorch va aparte.</summary>
    public static readonly IReadOnlyList<string> SeedVcPackages =
    [
        "scipy==1.13.1", "librosa==0.10.2", "huggingface-hub==0.36.2", "munch==4.0.0", "einops==0.8.0",
        "descript-audio-codec==1.0.0", "pydub==0.25.1", "transformers==4.46.3", "soundfile==0.12.1",
        "numpy==1.26.4", "hydra-core==1.3.2", "pyyaml==6.0.3", "python-dotenv==1.2.4", "accelerate==1.15.0",
    ];

    public static IReadOnlyList<DubbingEngineInfo> All { get; } =
    [
        new(DubbingEngine.Base, "Voces cubanas (edge-tts)",
            "Las voces cubanas de Microsoft (Manuel y Belkys). Imprescindible: es el motor que crea el doblaje. Necesita internet al generar.",
            "≈ 100 MB", false),
        new(DubbingEngine.SeedVc, "Voces propias (Seed-VC)",
            "Copia el timbre de una muestra de voz de 10–25 s (la tuya, la de un amigo con su permiso) manteniendo el acento cubano.",
            "≈ 7 GB", true),
        new(DubbingEngine.IndexTts, "Emociones (IndexTTS-2.5)",
            "Voces con ira, sorpresa, alegría, tristeza… según la emoción de cada línea. Más lento: unos 10 s por línea.",
            "≈ 12 GB", true),
    ];

    private readonly IProcessRunner _runner;
    private readonly HttpClient _http;

    public DubbingEngines(string? root = null, IProcessRunner? runner = null, HttpClient? http = null)
    {
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(root) ? DefaultRoot : root);
        _runner = runner ?? new ProcessRunner();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    public static string DefaultRoot => Path.Combine(AppPaths.Root, "voces");

    public string Root { get; }
    public string UvExe => Path.Combine(Root, "uv", "uv.exe");
    public string HfCache => Path.Combine(Root, "hf_cache");

    public string Folder(DubbingEngine e) => Path.Combine(Root, e switch
    {
        DubbingEngine.Base => "base",
        DubbingEngine.SeedVc => "seed-vc",
        _ => "index-tts",
    });

    public string Python(DubbingEngine e) => e == DubbingEngine.Base
        ? Path.Combine(Folder(e), "Scripts", "python.exe")
        : Path.Combine(Folder(e), ".venv", "Scripts", "python.exe");

    private string Marker(DubbingEngine e) => Path.Combine(Folder(e), ".instalado");

    private static string VersionOf(DubbingEngine e) => e switch
    {
        DubbingEngine.Base => "edge-tts " + EdgeTtsVersion,
        DubbingEngine.SeedVc => "seed-vc " + SeedVcCommit,
        _ => "index-tts " + IndexTtsCommit,
    };

    /// <summary>Instalado y completo (la instalación terminó y el Python del motor existe).</summary>
    public bool IsInstalled(DubbingEngine e) =>
        File.Exists(Python(e)) && File.Exists(Marker(e)) && File.ReadAllText(Marker(e)).Trim() == VersionOf(e);

    /// <summary>Variables para todos los procesos de los motores: cachés y Python gestionado dentro de la carpeta.</summary>
    public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>
    {
        ["HF_HUB_CACHE"] = HfCache,
        ["UV_CACHE_DIR"] = Path.Combine(Root, "uv-cache"),
        ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(Root, "python"),
        ["UV_LINK_MODE"] = "copy",
        // Siempre el Python que instala uv en la carpeta (no uno del sistema que se pueda desinstalar).
        ["UV_PYTHON_PREFERENCE"] = "only-managed",
        ["PYTHONIOENCODING"] = "utf-8",
        ["PYTHONUTF8"] = "1",
        ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1",
    };

    /// <summary>¿Hay una GPU NVIDIA? (nvidia-smi responde).</summary>
    public async Task<string?> DetectNvidiaGpuAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _runner.RunAsync("nvidia-smi", ["--query-gpu=name,memory.total", "--format=csv,noheader"], ct).ConfigureAwait(false);
            return r.ExitCode == 0 && r.StandardOutput.Trim().Length > 0 ? r.StandardOutput.Trim().Split('\n')[0].Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Instala un motor (Seed-VC e IndexTTS necesitan antes el base). Se puede repetir: lo ya descargado se reutiliza.
    /// <paramref name="scriptsDir"/>: carpeta con los scripts Python de la app (para la comprobación final).
    /// </summary>
    /// <exception cref="DubbingInstallException">Falló una descarga o un paso de la instalación.</exception>
    public async Task InstallAsync(DubbingEngine engine, string ffmpeg, string scriptsDir, IProgress<string> log, CancellationToken ct)
    {
        if (engine != DubbingEngine.Base && !IsInstalled(DubbingEngine.Base))
            throw new DubbingInstallException("Primero instala «Voces cubanas (edge-tts)».");
        Directory.CreateDirectory(Root);
        await EnsureUvAsync(log, ct).ConfigureAwait(false);
        var folder = Folder(engine);
        if (File.Exists(Marker(engine)))
            File.Delete(Marker(engine));
        switch (engine)
        {
            case DubbingEngine.Base:
                log.Report("Creando el entorno de Python 3.12…");
                await UvAsync(["venv", "--allow-existing", "--python", "3.12", folder], null, log, ct).ConfigureAwait(false);
                log.Report("Instalando edge-tts…");
                await UvAsync(["pip", "install", "--python", Python(engine), "edge-tts==" + EdgeTtsVersion], null, log, ct).ConfigureAwait(false);
                break;

            case DubbingEngine.SeedVc:
                await DownloadRepoAsync("Plachtaa/seed-vc", SeedVcCommit, folder, log, ct).ConfigureAwait(false);
                log.Report("Creando el entorno de Python 3.10…");
                await UvAsync(["venv", "--allow-existing", "--python", "3.10", Path.Combine(folder, ".venv")], null, log, ct).ConfigureAwait(false);
                log.Report("Instalando PyTorch con CUDA (≈ 3 GB, tarda)…");
                await UvAsync(["pip", "install", "--python", Python(engine), "torch==2.7.1", "torchaudio==2.7.1", "--index-url", TorchIndex],
                    null, log, ct).ConfigureAwait(false);
                log.Report("Instalando el resto de dependencias…");
                await UvAsync(["pip", "install", "--python", Python(engine), .. SeedVcPackages], null, log, ct).ConfigureAwait(false);
                break;

            case DubbingEngine.IndexTts:
                await DownloadRepoAsync("index-tts/index-tts", IndexTtsCommit, folder, log, ct).ConfigureAwait(false);
                log.Report("Instalando dependencias (PyTorch con CUDA incluido, ≈ 8 GB, tarda)…");
                await UvAsync(["sync"], folder, log, ct).ConfigureAwait(false);
                log.Report("Descargando el modelo IndexTTS-2.5 (≈ 4 GB)…");
                await RunPythonAsync(engine, [
                    "-c",
                    "from huggingface_hub import snapshot_download as d; " +
                    $"d('{IndexTtsModel}', revision='{IndexTtsModelRevision}', local_dir='checkpoints', ignore_patterns=['qwen0.6bemo4-merge/*'])",
                ], folder, log, ct).ConfigureAwait(false);
                break;
        }

        if (engine != DubbingEngine.Base)
        {
            log.Report("Comprobando el motor (la primera vez descarga modelos auxiliares)…");
            await WarmUpAsync(engine, ffmpeg, scriptsDir, log, ct).ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(Marker(engine), VersionOf(engine), ct).ConfigureAwait(false);
        log.Report("Listo: " + All.First(i => i.Engine == engine).Name + " instalado.");
    }

    /// <summary>Genera una frase de prueba con el motor recién instalado: valida la instalación y descarga los modelos.</summary>
    private async Task WarmUpAsync(DubbingEngine engine, string ffmpeg, string scriptsDir, IProgress<string> log, CancellationToken ct)
    {
        var dir = Path.Combine(Root, "prueba");
        Directory.CreateDirectory(dir);
        var mp3 = Path.Combine(dir, "voz.mp3");
        var wav = Path.Combine(dir, "voz.wav");
        await RunPythonAsync(DubbingEngine.Base, [
            "-m", "edge_tts", "--voice", CubanVoices.Male, "--text", "Asere, ¿qué volá? Esto es una prueba del doblaje.", "--write-media", mp3,
        ], dir, log, ct).ConfigureAwait(false);
        var conv = await _runner.RunAsync(ffmpeg, ["-y", "-v", "error", "-i", mp3, "-ac", "1", "-ar", "24000", wav], ct).ConfigureAwait(false);
        if (conv.ExitCode != 0)
            throw new DubbingInstallException("FFmpeg no pudo preparar el audio de prueba: " + conv.StandardError.Trim());
        var output = Path.Combine(dir, engine == DubbingEngine.SeedVc ? "vc.wav" : "idx.wav");
        var job = engine == DubbingEngine.SeedVc
            ? $$"""[{"source": {{Json(wav)}}, "target": {{Json(wav)}}, "output": {{Json(output)}}}]"""
            : $$"""[{"text": "Asere, ¿qué volá?", "speaker": {{Json(wav)}}, "emo": [0, 0.8, 0, 0, 0, 0, 0, 0], "output": {{Json(output)}}}]""";
        var jobs = Path.Combine(dir, "jobs.json");
        await File.WriteAllTextAsync(jobs, job, ct).ConfigureAwait(false);
        var script = Path.Combine(scriptsDir, engine == DubbingEngine.SeedVc ? "lote_seedvc.py" : "lote_indextts.py");
        await RunPythonAsync(engine, [script, Folder(engine), jobs], Folder(engine), log, ct).ConfigureAwait(false);
        if (!File.Exists(output))
            throw new DubbingInstallException("La prueba del motor no generó audio.");
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private async Task EnsureUvAsync(IProgress<string> log, CancellationToken ct)
    {
        if (File.Exists(UvExe))
            return;
        var url = $"https://github.com/astral-sh/uv/releases/download/{UvVersion}/uv-x86_64-pc-windows-msvc.zip";
        log.Report($"Descargando uv {UvVersion} (gestor de Python)…");
        var zip = Path.Combine(Root, "uv.zip");
        await DownloadAsync(url, zip, log, ct).ConfigureAwait(false);
        await using (var fs = File.OpenRead(zip))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
            if (!hash.Equals(UvSha256, StringComparison.OrdinalIgnoreCase))
                throw new DubbingInstallException("La descarga de uv no coincide con su huella SHA-256: se descarta.");
        }
        // Se extrae aparte y se mueve al final: un corte a mitad no deja un uv.exe a medias que parezca instalado.
        var dir = Path.GetDirectoryName(UvExe)!;
        var tmp = dir + ".tmp";
        if (Directory.Exists(tmp))
            Directory.Delete(tmp, recursive: true);
        ZipFile.ExtractToDirectory(zip, tmp);
        File.Delete(zip);
        if (!File.Exists(Path.Combine(tmp, "uv.exe")))
            throw new DubbingInstallException("El paquete de uv no contiene uv.exe.");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.Move(tmp, dir);
    }

    /// <summary>Descarga el código de un repositorio de GitHub en una versión fija y lo deja en <paramref name="folder"/>.</summary>
    private async Task DownloadRepoAsync(string repo, string commit, string folder, IProgress<string> log, CancellationToken ct)
    {
        var stamp = Path.Combine(folder, ".codigo");
        if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == commit)
        {
            log.Report($"Código de {repo} ya descargado.");
            return;
        }
        log.Report($"Descargando el código de {repo} ({commit[..7]})…");
        var zip = Path.Combine(Root, "repo.zip");
        await DownloadAsync($"https://github.com/{repo}/archive/{commit}.zip", zip, log, ct).ConfigureAwait(false);
        var tmp = Path.Combine(Root, "repo_tmp");
        if (Directory.Exists(tmp))
            Directory.Delete(tmp, recursive: true);
        ZipFile.ExtractToDirectory(zip, tmp);
        File.Delete(zip);
        var top = Directory.GetDirectories(tmp).Single();
        Directory.CreateDirectory(folder);
        // Se copia encima (si había una instalación a medias, su .venv y sus modelos se conservan).
        foreach (var file in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(folder, Path.GetRelativePath(top, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
        Directory.Delete(tmp, recursive: true);
        await File.WriteAllTextAsync(stamp, commit, ct).ConfigureAwait(false);
    }

    private async Task DownloadAsync(string url, string path, IProgress<string> log, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(path);
            var buffer = new byte[1 << 16];
            long done = 0, lastReport = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (done - lastReport > 8 << 20)
                {
                    lastReport = done;
                    log.Report(total is > 0 ? $"  {done >> 20} de {total >> 20} MB" : $"  {done >> 20} MB");
                }
            }
        }
        catch (HttpRequestException ex)
        {
            throw new DubbingInstallException($"No se pudo descargar {url}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DubbingInstallException($"La descarga de {url} tardó demasiado (sin respuesta). Inténtalo de nuevo.", ex);
        }
    }

    private Task UvAsync(IReadOnlyList<string> args, string? workingDirectory, IProgress<string> log, CancellationToken ct) =>
        RunLoggedAsync(UvExe, args, workingDirectory, log, ct);

    /// <summary>Ejecuta el Python de un motor con las variables de la carpeta de motores.</summary>
    public Task RunPythonAsync(DubbingEngine engine, IReadOnlyList<string> args, string workingDirectory, IProgress<string> log, CancellationToken ct) =>
        RunLoggedAsync(Python(engine), args, workingDirectory, log, ct);

    /// <summary>Las barras de progreso (tqdm, pip) se redibujan miles de veces: no van al registro.</summary>
    internal static bool IsWorthLogging(string line)
    {
        var t = line.Trim();
        return t.Length > 0 && !t.Contains("%|") && !t.Contains("it/s]") && !t.Contains("B/s]") && !t.StartsWith("Downloading ", StringComparison.Ordinal)
               && !(t.Contains("MiB/") && t.Contains("MiB"));
    }

    private async Task RunLoggedAsync(string exe, IReadOnlyList<string> args, string? workingDirectory, IProgress<string> log, CancellationToken ct)
    {
        var options = new ProcessRunOptions
        {
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = Environment,
            OnStandardOutputLine = l => { if (IsWorthLogging(l)) log.Report("  " + l.Trim()); },
            OnStandardErrorLine = l => { if (IsWorthLogging(l)) log.Report("  " + l.Trim()); },
        };
        ProcessResult r;
        try
        {
            r = await _runner.RunAsync(exe, args, options, ct).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new DubbingInstallException($"No se pudo ejecutar {Path.GetFileName(exe)}: {ex.Message}", ex);
        }
        if (r.ExitCode != 0)
        {
            var last = (r.StandardError + "\n" + r.StandardOutput).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            throw new DubbingInstallException($"{Path.GetFileName(exe)} terminó con error ({r.ExitCode}): {last}");
        }
    }
}
