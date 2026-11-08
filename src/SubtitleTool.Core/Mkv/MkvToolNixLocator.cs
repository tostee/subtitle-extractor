namespace SubtitleTool.Core.Mkv;

/// <summary>Rutas a los ejecutables de MKVToolNix.</summary>
public sealed record MkvToolNixPaths(string MkvMerge, string MkvExtract);

public sealed class MkvToolNixNotFoundException : Exception
{
    public MkvToolNixNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Busca mkvmerge/mkvextract en: %SUBTOOL_MKVTOOLNIX%, C:\Program Files\MKVToolNix,
/// C:\Program Files (x86)\MKVToolNix y el PATH (en ese orden).
/// </summary>
public static class MkvToolNixLocator
{
    public const string EnvironmentVariable = "SUBTOOL_MKVTOOLNIX";

    public static IReadOnlyList<string> CandidateDirectories(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var dirs = new List<string>();

        var custom = getEnv(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(custom))
            dirs.Add(custom.Trim().Trim('"'));

        dirs.Add(@"C:\Program Files\MKVToolNix");
        dirs.Add(@"C:\Program Files (x86)\MKVToolNix");

        var path = getEnv("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                dirs.Add(part.Trim().Trim('"'));
        }
        return dirs;
    }

    /// <summary>
    /// Como <see cref="Locate"/>, pero si <paramref name="directory"/> no está vacío se busca primero ahí
    /// (tiene la misma prioridad que <c>%SUBTOOL_MKVTOOLNIX%</c>, que sustituye).
    /// </summary>
    public static MkvToolNixPaths LocatePreferring(string? directory, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return Locate(fileExists: fileExists);
        return Locate(name => name == EnvironmentVariable ? directory : Environment.GetEnvironmentVariable(name), fileExists);
    }

    public static MkvToolNixPaths Locate(Func<string, string?>? getEnv = null, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var dirs = CandidateDirectories(getEnv);

        foreach (var dir in dirs)
        {
            string merge, extract;
            try
            {
                merge = Path.Combine(dir, "mkvmerge" + exe);
                extract = Path.Combine(dir, "mkvextract" + exe);
            }
            catch (ArgumentException)
            {
                continue; // entrada del PATH con caracteres inválidos
            }
            if (fileExists(merge) && fileExists(extract))
                return new MkvToolNixPaths(merge, extract);
        }

        throw new MkvToolNixNotFoundException(
            "No se encontró MKVToolNix (mkvmerge y mkvextract). Instálalo desde https://mkvtoolnix.download/ " +
            $"o indica su carpeta en la variable de entorno {EnvironmentVariable}. Se buscó en: " +
            string.Join("; ", dirs.Take(3)) + " y en el PATH.");
    }
}
