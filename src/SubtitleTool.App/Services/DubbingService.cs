using System.Text;
using SubtitleTool.App.Models;
using SubtitleTool.Core.Dubbing;
using SubtitleTool.Core.Media;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.App.Services;

/// <summary>Implementación real del doblaje: motores en la carpeta de los ajustes y scripts junto al ejecutable.</summary>
public sealed class DubbingService : IDubbingService
{
    /// <summary>Carpeta con motor_doblaje.py y los scripts de lotes (se copian junto a Subtitulos.exe).</summary>
    public static string ScriptsDirectory => Path.Combine(AppContext.BaseDirectory, "Dubbing");

    private static DubbingEngines Engines(AppSettings settings) => new(settings.DubbingEnginesDirectory);

    public string EnginesRoot(AppSettings settings) => Engines(settings).Root;

    public bool IsInstalled(DubbingEngine engine, AppSettings settings) => Engines(settings).IsInstalled(engine);

    public Task<string?> DetectGpuAsync(CancellationToken ct = default) => new DubbingEngines().DetectNvidiaGpuAsync(ct);

    public Task InstallAsync(DubbingEngine engine, AppSettings settings, IProgress<string> log, CancellationToken ct) =>
        Task.Run(() => Engines(settings).InstallAsync(engine, Ffmpeg(settings).FFmpeg, ScriptsDirectory, log, ct), ct);

    public IReadOnlyList<(string Path, string Label)> FindSubtitles(string videoPath)
    {
        var baseName = Path.ChangeExtension(Path.GetFullPath(videoPath), null);
        (string Suffix, string Label)[] candidates =
        [
            (".es-cu.srt", "Cubano"),
            (".es-cu.sdh.srt", "Cubano (SDH)"),
            (".es.srt", "Español"),
            (".es.sdh.srt", "Español (SDH)"),
        ];
        return candidates.Where(c => File.Exists(baseName + c.Suffix))
            .Select(c => (baseName + c.Suffix, $"{c.Label} — {Path.GetFileName(baseName + c.Suffix)}"))
            .ToList();
    }

    public DubbingProject LoadProject(string subtitlePath) => DubbingProjectStore.LoadOrCreate(subtitlePath);

    public void SaveProject(DubbingProject project) => DubbingProjectStore.Save(project);

    public Task<TokenUsage> AnnotateAsync(DubbingProject project, AppSettings settings, IProgress<AnnotationProgress> progress,
        CancellationToken ct) =>
        Task.Run(async () =>
        {
            var o = settings.ToProcessingOptions();
            var client = TranslationClientFactory.Create(o.Translation.Backend, o.Translation.Model, o.Translation.Effort, o.Timeout)
                         ?? throw new TranslationClientException(TranslationClientFactory.MissingApiKeyMessage);
            var usage = await new DubbingAnnotator(client).AnnotateAsync(project, LoadGlossary(project.SubtitlePath), progress, ct)
                .ConfigureAwait(false);
            DubbingProjectStore.Save(project);
            return usage;
        }, ct);

    /// <summary>Glosario de la película (<c>X.glossary.json</c>) si existe: ayuda a Claude con los nombres.</summary>
    private static Glossary? LoadGlossary(string subtitlePath)
    {
        var name = Path.GetFileName(subtitlePath);
        foreach (var suffix in new[] { ".es-cu.sdh.srt", ".es-cu.srt", ".es.sdh.srt", ".es.srt" })
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            var path = Path.Combine(Path.GetDirectoryName(subtitlePath)!, name[..^suffix.Length] + ".glossary.json");
            try
            {
                return File.Exists(path) ? TranslationPrompts.ParseGlossary(File.ReadAllText(path, Encoding.UTF8)) : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
        return null;
    }

    public Task<DubbingResult> RunAsync(DubbingRequest request, AppSettings settings, IProgress<DubbingProgress> progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            var ff = Ffmpeg(settings);
            return new DubbingRunner(Engines(settings), ScriptsDirectory).RunAsync(request, ff.FFmpeg, ff.FFprobe, progress, ct);
        }, ct);

    /// <exception cref="DubbingException">No hay FFmpeg.</exception>
    private static FfmpegPaths Ffmpeg(AppSettings settings)
    {
        try
        {
            return FfmpegLocator.LocatePreferring(settings.FfmpegDirectory);
        }
        catch (FfmpegNotFoundException)
        {
            throw new DubbingException("El doblaje necesita FFmpeg y no se encontró. Instálalo (winget install Gyan.FFmpeg) " +
                                       "o indica su carpeta en Ajustes.");
        }
    }
}
