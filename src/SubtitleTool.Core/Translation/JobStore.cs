using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubtitleTool.Core.Translation;

public sealed class JobInfo
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    /// <summary>Backend usado; vacío en trabajos antiguos (= api).</summary>
    [JsonPropertyName("backend")] public string Backend { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("chunkSize")] public int ChunkSize { get; set; }
    [JsonPropertyName("promptVersion")] public string PromptVersion { get; set; } = "";
    [JsonPropertyName("created")] public DateTimeOffset Created { get; set; }
}

public sealed class ChunkState
{
    /// <summary>true si todos los bloques del tramo están traducidos.</summary>
    [JsonPropertyName("complete")] public bool Complete { get; set; }
    [JsonPropertyName("items")] public List<TranslationItem> Items { get; set; } = [];
}

/// <summary>
/// Estado de reanudación en <c>&lt;raíz&gt;\&lt;hash&gt;\</c>: <c>job.json</c> + <c>chunk_0001.json</c>…
/// El hash depende de la ruta completa, el tamaño y la fecha de modificación del .en.srt.
/// </summary>
public sealed class JobStore
{
    public string Directory { get; }

    public JobStore(string jobsRoot, string englishPath)
    {
        Directory = Path.Combine(jobsRoot, ComputeHash(englishPath));
    }

    public static string ComputeHash(string englishPath)
    {
        var info = new FileInfo(Path.GetFullPath(englishPath));
        var key = string.Join("|",
            info.FullName.ToUpperInvariant(),
            info.Length.ToString(CultureInfo.InvariantCulture),
            info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    private string JobFile => Path.Combine(Directory, "job.json");
    private string ChunkFile(int chunkNumber) => Path.Combine(Directory, $"chunk_{chunkNumber:0000}.json");

    public bool Exists => File.Exists(JobFile);

    public JobInfo? LoadJob() => Read<JobInfo>(JobFile);

    public void SaveJob(JobInfo job) => Write(JobFile, job);

    public ChunkState? LoadChunk(int chunkNumber) => Read<ChunkState>(ChunkFile(chunkNumber));

    public void SaveChunk(int chunkNumber, ChunkState state) => Write(ChunkFile(chunkNumber), state);

    public void Delete()
    {
        if (System.IO.Directory.Exists(Directory))
            System.IO.Directory.Delete(Directory, recursive: true);
    }

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), TranslationPrompts.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Write<T>(string path, T value)
    {
        System.IO.Directory.CreateDirectory(Directory);
        // Escritura atómica: archivo temporal + reemplazo.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, TranslationPrompts.IndentedJsonOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
