using System.Globalization;
using System.Text;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Diagnostics;

/// <summary>
/// Registro de actividad y errores en <c>%LOCALAPPDATA%\SubtitleTool\logs\subtitulos-AAAA-MM-DD.log</c>
/// (uno por día, compartido por la ventana y la CLI). Escribir nunca lanza excepciones; se borran los de más de 30 días.
/// </summary>
public sealed class AppLog
{
    public const int KeepDays = 30;
    private static readonly object Sync = new();

    /// <summary>Registro global en la carpeta de logs de la aplicación.</summary>
    public static AppLog Default { get; } = new(AppPaths.Logs);

    private readonly string _directory;
    private readonly Func<DateTime> _now;
    private bool _cleaned;

    public AppLog(string directory, Func<DateTime>? now = null)
    {
        _directory = directory;
        _now = now ?? (() => DateTime.Now);
    }

    public string Directory => _directory;

    /// <summary>Archivo del día actual.</summary>
    public string CurrentFile => Path.Combine(_directory, $"subtitulos-{_now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");

    /// <param name="source">Quién escribe: "app", "cli"…</param>
    public void Write(string source, string message)
    {
        var line = $"{_now().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} [{source}] {message}";
        try
        {
            lock (Sync)
            {
                System.IO.Directory.CreateDirectory(_directory);
                AppendShared(CurrentFile, line + Environment.NewLine);
                if (!_cleaned)
                {
                    _cleaned = true;
                    DeleteOld();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Un fallo del registro nunca debe romper la aplicación.
        }
    }

    /// <summary>Error con la traza completa (para diagnosticar errores inesperados).</summary>
    public void WriteException(string source, string context, Exception exception) =>
        Write(source, $"ERROR {context}: {exception}");

    /// <summary>La ventana y la CLI pueden escribir a la vez en el mismo archivo: se comparte y se reintenta.</summary>
    private static void AppendShared(string path, string text)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    private void DeleteOld()
    {
        var limit = _now().Date.AddDays(-KeepDays);
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "subtitulos-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["subtitulos-".Length..];
            if (DateTime.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < limit)
            {
                try { File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
