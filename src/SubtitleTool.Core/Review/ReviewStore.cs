using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubtitleTool.Core.Srt;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Review;

/// <summary>Datos de un borrador pendiente de revisión.</summary>
public sealed class ReviewDraftInfo
{
    [JsonPropertyName("englishPath")] public string EnglishPath { get; set; } = "";
    [JsonPropertyName("spanishPath")] public string SpanishPath { get; set; } = "";
    [JsonPropertyName("plainCopyPath")] public string PlainCopyPath { get; set; } = "";
    /// <summary>Crear la copia .srt sin idioma al guardar (si no existe), como se pidió al traducir.</summary>
    [JsonPropertyName("createPlainCopy")] public bool CreatePlainCopy { get; set; }
    /// <summary>El borrador se hizo con --force sobre un .es.srt existente: al guardar lo reemplaza (con copia .bak).</summary>
    [JsonPropertyName("replacesExisting")] public bool ReplacesExisting { get; set; }
    /// <summary>Huella SHA-256 del .en.srt con el que se tradujo: si cambia, el borrador ya no encaja.</summary>
    [JsonPropertyName("englishSha256")] public string EnglishSha256 { get; set; } = "";
    [JsonPropertyName("created")] public DateTimeOffset Created { get; set; }
    [JsonPropertyName("updated")] public DateTimeOffset Updated { get; set; }
    /// <summary>Ids (posición 1-based) de los bloques que el usuario ha editado.</summary>
    [JsonPropertyName("edited")] public List<int> Edited { get; set; } = [];
}

public enum DraftState
{
    None,
    /// <summary>Borrador listo para revisar.</summary>
    Valid,
    /// <summary>El .en.srt ya no existe o cambió desde que se tradujo: el borrador no encaja y hay que descartarlo.</summary>
    Stale,
}

/// <summary>
/// Borradores de traducciones pendientes de revisión, en <c>%LOCALAPPDATA%\SubtitleTool\revisiones\&lt;hash&gt;\</c>
/// (<c>borrador.es.srt</c> + <c>info.json</c>). Se guardan fuera de la carpeta del video para que el reproductor no los
/// tome por subtítulos terminados. El hash de la carpeta depende solo de la ruta del .en.srt.
/// </summary>
public sealed class ReviewStore
{
    public string Root { get; }

    public ReviewStore(string? root = null) => Root = root ?? Path.Combine(AppPaths.Root, "revisiones");

    public static ReviewStore Default { get; } = new();

    public string DirectoryFor(string englishPath)
    {
        var key = Path.GetFullPath(englishPath).ToUpperInvariant();
        return Path.Combine(Root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant());
    }

    public string DraftPath(string englishPath) => Path.Combine(DirectoryFor(englishPath), "borrador.es.srt");
    private string InfoPath(string englishPath) => Path.Combine(DirectoryFor(englishPath), "info.json");

    /// <summary>Hay un borrador (válido o no).</summary>
    public bool HasDraft(string? englishPath) =>
        englishPath is not null && File.Exists(DraftPath(englishPath)) && File.Exists(InfoPath(englishPath));

    /// <summary>Hay un borrador y encaja con el .en.srt actual.</summary>
    public bool HasValidDraft(string? englishPath) => englishPath is not null && GetState(englishPath) == DraftState.Valid;

    public DraftState GetState(string englishPath)
    {
        if (!HasDraft(englishPath))
            return DraftState.None;
        if (!File.Exists(englishPath))
            return DraftState.Stale;
        var info = LoadInfo(englishPath);
        if (info is null)
            return DraftState.Stale;
        // Borradores sin huella (no debería haberlos): se aceptan si el inglés existe.
        return info.EnglishSha256.Length == 0 || string.Equals(info.EnglishSha256, Fingerprint(englishPath), StringComparison.OrdinalIgnoreCase)
            ? DraftState.Valid
            : DraftState.Stale;
    }

    public static string Fingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public ReviewDraftInfo? LoadInfo(string englishPath)
    {
        try
        {
            return File.Exists(InfoPath(englishPath))
                ? JsonSerializer.Deserialize<ReviewDraftInfo>(File.ReadAllText(InfoPath(englishPath), Encoding.UTF8))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Guarda (o reemplaza) el borrador. Escritura atómica: primero el SRT, luego info.json.</summary>
    /// <param name="replacesExisting">null = conservar lo que dijera el borrador anterior.</param>
    public async Task SaveDraftAsync(TranslationPaths paths, SrtDocument translated, bool createPlainCopy,
        IEnumerable<int>? edited = null, bool? replacesExisting = null, CancellationToken ct = default)
    {
        var dir = DirectoryFor(paths.EnglishPath);
        Directory.CreateDirectory(dir);
        var draft = DraftPath(paths.EnglishPath);
        var tmp = draft + ".tmp";
        await SrtWriter.WriteFileAsync(tmp, translated, ct).ConfigureAwait(false);
        File.Move(tmp, draft, overwrite: true);

        var now = DateTimeOffset.Now;
        var info = LoadInfo(paths.EnglishPath) ?? new ReviewDraftInfo { Created = now };
        info.EnglishPath = paths.EnglishPath;
        info.SpanishPath = paths.SpanishPath;
        info.PlainCopyPath = paths.PlainCopyPath;
        info.CreatePlainCopy = createPlainCopy;
        info.EnglishSha256 = Fingerprint(paths.EnglishPath);
        info.Updated = now;
        if (replacesExisting is { } replaces)
            info.ReplacesExisting = replaces;
        if (edited is not null)
            info.Edited = edited.Distinct().Order().ToList();
        var infoTmp = InfoPath(paths.EnglishPath) + ".tmp";
        await File.WriteAllTextAsync(infoTmp, JsonSerializer.Serialize(info, TranslationPrompts.IndentedJsonOptions),
            new UTF8Encoding(false), ct).ConfigureAwait(false);
        File.Move(infoTmp, InfoPath(paths.EnglishPath), overwrite: true);
    }

    public void Delete(string englishPath)
    {
        var dir = DirectoryFor(englishPath);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
