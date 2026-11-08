using System.Text.Json.Serialization;
using SubtitleTool.Core.Media;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.App.Models;

/// <summary>Ajustes de la interfaz (se guardan en settings.json; nunca incluyen la clave de API).</summary>
public sealed class AppSettings
{
    [JsonPropertyName("backend")] public string Backend { get; set; } = TranslationBackends.ClaudeCode;
    [JsonPropertyName("model")] public string Model { get; set; } = ModelPricing.DefaultModel;
    [JsonPropertyName("effort")] public string Effort { get; set; } = "medium";
    [JsonPropertyName("chunkSize")] public int ChunkSize { get; set; } = 150;
    [JsonPropertyName("timeoutMinutes")] public int TimeoutMinutes { get; set; } = 10;
    [JsonPropertyName("createPlainCopy")] public bool CreatePlainCopy { get; set; } = true;
    /// <summary>No escribir el .es.srt al traducir: dejar un borrador para revisarlo lado a lado.</summary>
    [JsonPropertyName("reviewBeforeSave")] public bool ReviewBeforeSave { get; set; }
    /// <summary>Carpeta de MKVToolNix elegida por el usuario; vacía = autodetección.</summary>
    [JsonPropertyName("mkvToolNixDirectory")] public string? MkvToolNixDirectory { get; set; }
    /// <summary>Carpeta de FFmpeg (ffmpeg.exe y ffprobe.exe, solo para .mp4); vacía = autodetección.</summary>
    [JsonPropertyName("ffmpegDirectory")] public string? FfmpegDirectory { get; set; }

    /// <summary>Buscar versiones nuevas en GitHub al abrir la app.</summary>
    [JsonPropertyName("checkForUpdates")] public bool CheckForUpdates { get; set; } = true;

    [JsonIgnore] public MediaToolDirectories Tools => new(MkvToolNixDirectory, FfmpegDirectory);

    [JsonIgnore] public bool IsClaudeCode => Backend == TranslationBackends.ClaudeCode;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Corrige valores desconocidos o fuera de rango (p. ej. un settings.json editado a mano).</summary>
    public AppSettings Normalize()
    {
        if (Choices.Backends.All(c => c.Value != Backend)) Backend = TranslationBackends.ClaudeCode;
        if (!ModelPricing.Known.ContainsKey(Model)) Model = ModelPricing.DefaultModel;
        if (Choices.Efforts.All(c => c.Value != Effort)) Effort = "medium";
        if (ChunkSize < 1) ChunkSize = 150;
        if (TimeoutMinutes < 1) TimeoutMinutes = 10;
        if (string.IsNullOrWhiteSpace(MkvToolNixDirectory)) MkvToolNixDirectory = null;
        if (string.IsNullOrWhiteSpace(FfmpegDirectory)) FfmpegDirectory = null;
        return this;
    }

    public VideoProcessingOptions ToProcessingOptions() => new()
    {
        Translation = new TranslationOptions
        {
            Backend = Backend,
            Model = Model,
            Effort = Effort,
            ChunkSize = ChunkSize,
            NoPlainCopy = !CreatePlainCopy,
            ReviewBeforeSave = ReviewBeforeSave,
        },
        Timeout = TimeSpan.FromMinutes(TimeoutMinutes),
    };
}

public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Opciones de los selectores.</summary>
public static class Choices
{
    public static IReadOnlyList<Choice> Backends { get; } =
    [
        new(TranslationBackends.ClaudeCode, "Suscripción"),
        new(TranslationBackends.Api, "API"),
    ];

    public static IReadOnlyList<Choice> Models { get; } =
    [
        new("claude-opus-5-5", "Opus 5.5"),
        new("claude-sonnet-5-5", "Sonnet 5.5"),
        new("claude-haiku-5-5", "Haiku 5.5"),
    ];

    public static IReadOnlyList<Choice> Efforts { get; } =
    [
        new("low", "Bajo (low)"),
        new("medium", "Medio (medium)"),
        new("high", "Alto (high)"),
    ];

    public static string Label(IReadOnlyList<Choice> list, string value) =>
        list.FirstOrDefault(c => c.Value == value)?.Label ?? value;
}
