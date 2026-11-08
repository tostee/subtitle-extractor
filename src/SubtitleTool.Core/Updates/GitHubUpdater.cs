using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubtitleTool.Core.Updates;

/// <summary>Versión publicada en GitHub Releases.</summary>
/// <param name="ChecksumUrl">Archivo <c>&lt;instalador&gt;.sha256</c> publicado junto al instalador (si existe).</param>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string Name,
    string Notes,
    string PageUrl,
    string? InstallerUrl,
    string? InstallerName,
    long InstallerSize,
    string? ChecksumUrl);

public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Busca, descarga e instala versiones nuevas desde GitHub Releases (repositorio público, sin token).
/// Solo se tienen en cuenta las versiones publicadas (no borradores ni preliminares) con una etiqueta <c>vX.Y.Z</c>.
/// </summary>
public sealed class GitHubUpdater
{
    public const string Repository = "tostee/subtitle-extractor";
    public const string InstallerPrefix = "Subtitulos-Setup-";

    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    private readonly HttpClient _http;
    private readonly string _repository;

    public GitHubUpdater(HttpClient? http = null, string repository = Repository)
    {
        _repository = repository;
        _http = http ?? CreateClient();
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Subtitulos-Updater", CurrentVersion.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>Versión de esta compilación (la <c>&lt;Version&gt;</c> de Directory.Build.props).</summary>
    public static Version CurrentVersion => Normalize(typeof(GitHubUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>"v1.2.3" o "1.2" → 1.2.3 / 1.2.0; null si la etiqueta no es una versión.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        var text = tag.Trim().TrimStart('v', 'V');
        var dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0)
            return null; // 1.2.3-beta: preliminar, no se ofrece
        return Version.TryParse(text, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Lee la respuesta de <c>GET /repos/{repo}/releases/latest</c>.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        var dto = JsonSerializer.Deserialize<ReleaseDto>(json);
        if (dto is null || dto.Draft || dto.Prerelease || ParseVersion(dto.TagName) is not { } version)
            return null;
        var assets = dto.Assets ?? [];
        var installer = assets.FirstOrDefault(a =>
            a.Name is not null
            && a.Name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var checksum = installer is null
            ? null
            : assets.FirstOrDefault(a => string.Equals(a.Name, installer.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        return new ReleaseInfo(version, dto.TagName!, string.IsNullOrWhiteSpace(dto.Name) ? dto.TagName! : dto.Name!,
            dto.Body ?? "", dto.HtmlUrl ?? ReleasesPage, installer?.DownloadUrl, installer?.Name, installer?.Size ?? 0, checksum?.DownloadUrl);
    }

    /// <summary>Última versión publicada, o null si no hay ninguna.</summary>
    /// <exception cref="UpdateException">Sin conexión, GitHub no responde o la respuesta no es válida.</exception>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _http.GetAsync($"https://api.github.com/repos/{_repository}/releases/latest", cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub respondió {(int)response.StatusCode} {response.ReasonPhrase}.");
            return ParseRelease(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException("GitHub no respondió a tiempo.");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("No se pudo consultar GitHub: " + ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new UpdateException("Respuesta de GitHub no válida: " + ex.Message, ex);
        }
    }

    /// <summary>Versión más nueva que la actual, o null si ya está al día.</summary>
    public async Task<ReleaseInfo?> FindNewerAsync(CancellationToken ct = default) =>
        await GetLatestAsync(ct).ConfigureAwait(false) is { } latest && latest.Version > CurrentVersion ? latest : null;

    /// <summary>Descarga el instalador a <c>%TEMP%\SubtitleTool-update</c> y comprueba su tamaño y su SHA-256.</summary>
    /// <param name="progress">Fracción descargada (0–1).</param>
    public async Task<string> DownloadInstallerAsync(ReleaseInfo release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (release.InstallerUrl is null || release.InstallerName is null)
            throw new UpdateException($"La versión {release.Tag} no incluye el instalador ({InstallerPrefix}*.exe).");

        var dir = Path.Combine(Path.GetTempPath(), "SubtitleTool-update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(release.InstallerName));
        var partial = target + ".descargando";
        try
        {
            string? expectedHash = null;
            if (release.ChecksumUrl is not null)
                expectedHash = ParseChecksum(await _http.GetStringAsync(release.ChecksumUrl, ct).ConfigureAwait(false));

            using (var response = await _http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? release.InstallerSize;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                        progress?.Report(Math.Min(1, (double)done / total));
                }
            }

            var length = new FileInfo(partial).Length;
            if (release.InstallerSize > 0 && length != release.InstallerSize)
                throw new UpdateException($"La descarga está incompleta ({length} de {release.InstallerSize} bytes).");
            if (expectedHash is not null)
            {
                var actual = await ComputeSha256Async(partial, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException("La huella SHA-256 del instalador descargado no coincide: no se instalará.");
            }
            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("No se pudo descargar la actualización: " + ex.Message, ex);
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Primer campo de un archivo .sha256 ("hash  nombre" o solo "hash").</summary>
    public static string ParseChecksum(string text)
    {
        var hash = text.Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new UpdateException("El archivo de huella SHA-256 publicado no es válido.");
        return hash.ToLowerInvariant();
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ instalación

    /// <summary>true si esta copia se instaló con el instalador (tiene desinstalador); en desarrollo no se autoactualiza.</summary>
    public static bool IsInstalled(string? appDirectory = null) =>
        File.Exists(Path.Combine(appDirectory ?? AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>Instalación por usuario (en %LOCALAPPDATA%) o para todos (Program Files, pide administrador).</summary>
    public static bool IsPerUserInstall(string? appDirectory = null)
    {
        var dir = Path.GetFullPath(appDirectory ?? AppContext.BaseDirectory);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return !string.IsNullOrEmpty(local) && dir.StartsWith(local, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Argumentos del instalador para actualizar sin preguntas sobre la instalación existente.</summary>
    /// <param name="relaunchApp">Volver a abrir la ventana al terminar (el instalador lo hace con <c>/UPDATE=1</c>).</param>
    public static IReadOnlyList<string> InstallerArguments(bool perUser, bool relaunchApp)
    {
        var args = new List<string> { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", perUser ? "/CURRENTUSER" : "/ALLUSERS" };
        if (relaunchApp)
            args.Add("/UPDATE=1");
        return args;
    }

    /// <summary>Lanza el instalador descargado (la aplicación debe cerrarse justo después).</summary>
    public static void LaunchInstaller(string installerPath, bool relaunchApp, string? appDirectory = null)
    {
        var psi = new ProcessStartInfo(installerPath) { UseShellExecute = true };
        foreach (var a in InstallerArguments(IsPerUserInstall(appDirectory), relaunchApp))
            psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<AssetDto>? Assets { get; set; }
    }

    private sealed class AssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; set; }
    }
}
