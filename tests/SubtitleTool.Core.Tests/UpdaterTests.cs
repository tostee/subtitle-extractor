using System.Net;
using System.Security.Cryptography;
using System.Text;
using SubtitleTool.Core.Updates;

namespace SubtitleTool.Core.Tests;

public sealed class UpdaterTests
{
    private const string Repo = "tostee/subtitle-extractor";

    private static string ReleaseJson(string tag, bool draft = false, bool prerelease = false, string assets = "") => $$"""
        {"tag_name":"{{tag}}","name":"Subtítulos {{tag}}","body":"- Novedades","html_url":"https://github.com/{{Repo}}/releases/tag/{{tag}}",
         "draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"assets":[{{assets}}]}
        """;

    private static string Asset(string name, long size) =>
        $$"""{"name":"{{name}}","size":{{size}},"browser_download_url":"https://dl/{{name}}"}""";

    /// <summary>Servidor HTTP falso: URL → (código, contenido).</summary>
    private sealed class FakeHandler(Dictionary<string, (HttpStatusCode Code, byte[] Body)> routes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var (code, body) = routes.TryGetValue(url, out var r) ? r : (HttpStatusCode.NotFound, Array.Empty<byte>());
            return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(body) });
        }
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V2.0", "2.0.0")]
    [InlineData("1.0.0.7", "1.0.0")]
    [InlineData("v1.3.0-beta", null)]
    [InlineData("latest", null)]
    [InlineData("", null)]
    public void ParseVersion(string tag, string? expected) =>
        Assert.Equal(expected is null ? null : Version.Parse(expected), GitHubUpdater.ParseVersion(tag));

    [Fact]
    public void ParseRelease_FindsInstallerAndChecksum()
    {
        var json = ReleaseJson("v1.4.0", assets: string.Join(",",
            Asset("notas.txt", 3), Asset("Subtitulos-Setup-1.4.0.exe", 1234), Asset("Subtitulos-Setup-1.4.0.exe.sha256", 90)));
        var r = GitHubUpdater.ParseRelease(json)!;

        Assert.Equal(new Version(1, 4, 0), r.Version);
        Assert.Equal("Subtitulos-Setup-1.4.0.exe", r.InstallerName);
        Assert.Equal(1234, r.InstallerSize);
        Assert.Equal("https://dl/Subtitulos-Setup-1.4.0.exe.sha256", r.ChecksumUrl);
        Assert.Equal("- Novedades", r.Notes);
    }

    [Fact]
    public void ParseRelease_IgnoresDraftsPrereleasesAndBadTags()
    {
        Assert.Null(GitHubUpdater.ParseRelease(ReleaseJson("v9.0.0", draft: true)));
        Assert.Null(GitHubUpdater.ParseRelease(ReleaseJson("v9.0.0", prerelease: true)));
        Assert.Null(GitHubUpdater.ParseRelease(ReleaseJson("nightly")));
        Assert.Null(GitHubUpdater.ParseRelease(ReleaseJson("v9.0.0"))!.InstallerUrl);
    }

    [Fact]
    public void ParseChecksum_AcceptsShaSumFormats_RejectsGarbage()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, GitHubUpdater.ParseChecksum(hash.ToUpperInvariant() + "  Subtitulos-Setup-1.0.0.exe\n"));
        Assert.Equal(hash, GitHubUpdater.ParseChecksum(hash + " *Subtitulos-Setup-1.0.0.exe"));
        Assert.Throws<UpdateException>(() => GitHubUpdater.ParseChecksum("<html>Not Found</html>"));
    }

    [Fact]
    public void InstallerArguments_KeepInstallScope_AndRelaunchOnlyFromApp()
    {
        Assert.Equal(["/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/CURRENTUSER", "/UPDATE=1"],
            GitHubUpdater.InstallerArguments(perUser: true, relaunchApp: true));
        Assert.Equal(["/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/ALLUSERS"],
            GitHubUpdater.InstallerArguments(perUser: false, relaunchApp: false));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.True(GitHubUpdater.IsPerUserInstall(Path.Combine(local, "Programs", "Subtitulos")));
        Assert.False(GitHubUpdater.IsPerUserInstall(@"C:\Program Files\Subtitulos"));
    }

    [Fact]
    public async Task GetLatest_NoReleases_IsNull_AndFindNewerComparesVersions()
    {
        var api = $"https://api.github.com/repos/{Repo}/releases/latest";
        var none = new GitHubUpdater(new HttpClient(new FakeHandler([])), Repo);
        Assert.Null(await none.GetLatestAsync());

        var older = new GitHubUpdater(new HttpClient(new FakeHandler(new() { [api] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v0.0.1"))) })), Repo);
        Assert.NotNull(await older.GetLatestAsync());
        Assert.Null(await older.FindNewerAsync());

        var newer = new GitHubUpdater(new HttpClient(new FakeHandler(new() { [api] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v999.0.0"))) })), Repo);
        Assert.Equal(new Version(999, 0, 0), (await newer.FindNewerAsync())!.Version);

        var broken = new GitHubUpdater(new HttpClient(new FakeHandler(new() { [api] = (HttpStatusCode.Forbidden, []) })), Repo);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => broken.GetLatestAsync());
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task Download_VerifiesSha256_AndSize()
    {
        var name = $"Subtitulos-Setup-9.{Guid.NewGuid():N}.exe".Replace("-9.", "-9.0."); // nombre único por ejecución
        var payload = Encoding.UTF8.GetBytes("instalador falso " + Guid.NewGuid());
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var routes = new Dictionary<string, (HttpStatusCode, byte[])>
        {
            [$"https://dl/{name}"] = (HttpStatusCode.OK, payload),
            [$"https://dl/{name}.sha256"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes($"{hash}  {name}\n")),
        };
        var release = new ReleaseInfo(new Version(9, 0, 0), "v9.0.0", "v9.0.0", "", "https://p", $"https://dl/{name}", name, payload.Length, $"https://dl/{name}.sha256");
        var updater = new GitHubUpdater(new HttpClient(new FakeHandler(routes)), Repo);

        var reported = new List<double>();
        var path = await updater.DownloadInstallerAsync(release, new SyncProgress(reported.Add));
        try
        {
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            Assert.Equal(1.0, reported[^1]);
        }
        finally
        {
            File.Delete(path);
        }

        routes[$"https://dl/{name}.sha256"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(new string('0', 64)));
        var bad = await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadInstallerAsync(release));
        Assert.Contains("SHA-256", bad.Message);
        Assert.False(File.Exists(path));

        var truncated = release with { InstallerSize = payload.Length + 1, ChecksumUrl = null };
        await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadInstallerAsync(truncated));
        Assert.False(File.Exists(path));
    }

    private sealed class SyncProgress(Action<double> action) : IProgress<double>
    {
        public void Report(double value) => action(value);
    }
}
