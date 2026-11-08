using SubtitleTool.Core.Updates;

namespace SubtitleTool.Cli;

/// <summary><c>subtool version</c> y <c>subtool update [--check]</c>.</summary>
internal static class UpdateCommands
{
    public static int Version()
    {
        Console.WriteLine($"subtool {GitHubUpdater.CurrentVersion.ToString(3)}");
        return ExitCodes.Ok;
    }

    public static async Task<int> UpdateAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var a = ParsedArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "check" });
        a.RequirePositionals(0, "subtool update [--check]");
        var current = GitHubUpdater.CurrentVersion;
        Console.WriteLine($"Versión instalada: {current.ToString(3)}. Consultando {GitHubUpdater.ReleasesPage} …");

        var updater = new GitHubUpdater();
        ReleaseInfo? latest;
        try
        {
            latest = await updater.GetLatestAsync(ct);
        }
        catch (UpdateException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failure;
        }

        if (latest is null || latest.Version <= current)
        {
            Console.WriteLine("✅ Ya tienes la versión más reciente.");
            return ExitCodes.Ok;
        }

        Console.WriteLine($"⬆️  Hay una versión nueva: {latest.Version.ToString(3)} ({latest.PageUrl})");
        if (a.Has("check"))
            return ExitCodes.Ok;
        if (!GitHubUpdater.IsInstalled())
        {
            Console.WriteLine("Esta copia no se instaló con el instalador (compilación de desarrollo): descárgala desde la página de arriba.");
            return ExitCodes.Ok;
        }

        try
        {
            var lastShown = -1;
            var progress = new Progress<double>(p =>
            {
                var pct = (int)(p * 100);
                if (pct / 10 != lastShown / 10)
                {
                    lastShown = pct;
                    Console.WriteLine($"   descargando… {pct}%");
                }
            });
            var installer = await updater.DownloadInstallerAsync(latest, progress, ct);
            Console.WriteLine("Instalador verificado. Se instala en segundo plano; esta consola termina ahora.");
            GitHubUpdater.LaunchInstaller(installer, relaunchApp: false);
            return ExitCodes.Ok;
        }
        catch (UpdateException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }
}
