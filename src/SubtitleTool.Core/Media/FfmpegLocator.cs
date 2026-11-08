namespace SubtitleTool.Core.Media;

/// <summary>Rutas a los ejecutables de FFmpeg (solo se usan para .mp4/.m4v).</summary>
public sealed record FfmpegPaths(string FFmpeg, string FFprobe);

public sealed class FfmpegNotFoundException : Exception
{
    public FfmpegNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Busca ffmpeg/ffprobe en: %SUBTOOL_FFMPEG% (o la carpeta de los ajustes), carpetas habituales
/// (C:\ffmpeg\bin, Program Files, winget, Chocolatey, Scoop) y el PATH del proceso y del registro (en ese orden).
/// </summary>
public static class FfmpegLocator
{
    public const string EnvironmentVariable = "SUBTOOL_FFMPEG";

    /// <summary>Paquete de winget recomendado (también el que propone el instalador).</summary>
    public const string WingetPackage = "Gyan.FFmpeg";

    /// <param name="extraDirectories">Carpetas habituales (null = las reales de este equipo).</param>
    /// <param name="registryPath">PATH de usuario y de sistema guardado en el registro (null = el real): si se instaló ffmpeg
    /// después de abrir la app, el PATH del proceso todavía no lo incluye.</param>
    public static IReadOnlyList<string> CandidateDirectories(Func<string, string?>? getEnv = null,
        Func<IEnumerable<string>>? extraDirectories = null, Func<string?>? registryPath = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        extraDirectories ??= DefaultExtraDirectories;
        registryPath ??= ReadRegistryPath;
        var dirs = new List<string>();

        var custom = getEnv(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var dir = custom.Trim().Trim('"');
            dirs.Add(dir);
            dirs.Add(Path.Combine(dir, "bin")); // se acepta la carpeta raíz de una descarga de ffmpeg
        }

        dirs.AddRange(extraDirectories());

        foreach (var path in new[] { getEnv("PATH"), registryPath() })
        {
            if (string.IsNullOrEmpty(path))
                continue;
            foreach (var part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                dirs.Add(part.Trim().Trim('"'));
        }
        return dirs.Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Como <see cref="Locate"/>, pero si <paramref name="directory"/> no está vacío se busca primero ahí.</summary>
    public static FfmpegPaths LocatePreferring(string? directory, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return Locate(fileExists: fileExists);
        return Locate(name => name == EnvironmentVariable ? directory : Environment.GetEnvironmentVariable(name), fileExists);
    }

    public static FfmpegPaths Locate(Func<string, string?>? getEnv = null, Func<string, bool>? fileExists = null,
        Func<IEnumerable<string>>? extraDirectories = null, Func<string?>? registryPath = null)
    {
        fileExists ??= File.Exists;
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var dirs = CandidateDirectories(getEnv, extraDirectories, registryPath);

        foreach (var dir in dirs)
        {
            string ffmpeg, ffprobe;
            try
            {
                ffmpeg = Path.Combine(dir, "ffmpeg" + exe);
                ffprobe = Path.Combine(dir, "ffprobe" + exe);
            }
            catch (ArgumentException)
            {
                continue; // entrada del PATH con caracteres inválidos
            }
            if (fileExists(ffmpeg) && fileExists(ffprobe))
                return new FfmpegPaths(ffmpeg, ffprobe);
        }

        throw new FfmpegNotFoundException(
            "No se encontró FFmpeg (ffmpeg y ffprobe), necesario para los videos .mp4. " +
            $"Instálalo con «winget install {WingetPackage}» o desde https://ffmpeg.org/download.html, " +
            $"o indica su carpeta en Ajustes o en la variable de entorno {EnvironmentVariable}.");
    }

    private static string? ReadRegistryPath()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
        return string.Join(Path.PathSeparator, new[] { user, machine }.Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>Carpetas donde suelen instalar ffmpeg las descargas manuales, winget, Chocolatey y Scoop.</summary>
    private static IEnumerable<string> DefaultExtraDirectories()
    {
        var dirs = new List<string> { @"C:\ffmpeg\bin" };
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf))
            dirs.Add(Path.Combine(pf, "ffmpeg", "bin"));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            dirs.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
            // winget sin enlaces: %LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg_…\ffmpeg-X-full_build\bin
            dirs.AddRange(SafeGlob(Path.Combine(local, "Microsoft", "WinGet", "Packages"), "Gyan.FFmpeg*", "ffmpeg-*"));
        }
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
            dirs.Add(Path.Combine(programData, "chocolatey", "bin"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            dirs.Add(Path.Combine(home, "scoop", "shims"));
        return dirs;
    }

    private static IEnumerable<string> SafeGlob(string root, string packagePattern, string versionPattern)
    {
        try
        {
            if (!Directory.Exists(root))
                return [];
            return Directory.EnumerateDirectories(root, packagePattern)
                .SelectMany(p => Directory.EnumerateDirectories(p, versionPattern))
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "bin"))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
