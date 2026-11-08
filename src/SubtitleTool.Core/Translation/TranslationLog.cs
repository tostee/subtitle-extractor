using System.Globalization;
using System.Text;

namespace SubtitleTool.Core.Translation;

/// <summary>Registro de un trabajo de traducción (nunca contiene la clave de API).</summary>
public interface ITranslationLog
{
    string? Path { get; }
    void Write(string message);
}

public sealed class NullTranslationLog : ITranslationLog
{
    public static readonly NullTranslationLog Instance = new();
    public string? Path => null;
    public void Write(string message) { }
}

/// <summary>Log en archivo: una línea por evento con marca de tiempo.</summary>
public sealed class FileTranslationLog : ITranslationLog
{
    private readonly object _lock = new();

    public string Path { get; }

    public FileTranslationLog(string logsDirectory, string jobName)
    {
        Directory.CreateDirectory(logsDirectory);
        var safe = string.Concat(jobName.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        Path = System.IO.Path.Combine(logsDirectory,
            $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}_{safe}.log");
    }

    public void Write(string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {message}{Environment.NewLine}";
        lock (_lock)
            File.AppendAllText(Path, line, new UTF8Encoding(false));
    }
}
