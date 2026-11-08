using SubtitleTool.Core.Mkv;
using SubtitleTool.Core.Processes;

namespace SubtitleTool.Core.Media;

/// <summary>
/// Extracción segura común a mkvextract y ffmpeg: la herramienta escribe en un archivo temporal de la misma carpeta y solo
/// si termina bien se mueve al nombre final sin sobrescribir. Así, cancelar o fallar nunca deja un subtítulo a medias con el
/// nombre definitivo (que luego se tomaría por bueno) ni borra uno que ya existía.
/// </summary>
internal static class SafeExtraction
{
    /// <summary>Temporal junto a la salida, con la misma extensión (ffmpeg deduce el formato de ella).</summary>
    public static string TempPathFor(string outputPath)
    {
        var full = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(full) ?? ".";
        return Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(full)}.parcial-{Guid.NewGuid():N}{Path.GetExtension(full)}");
    }

    /// <param name="run">Ejecuta la herramienta escribiendo en la ruta temporal recibida.</param>
    /// <param name="failure">Mensaje de error si el código de salida indica fallo; null si fue bien.</param>
    /// <param name="successMessage">Mensaje si fue bien (según el código de salida).</param>
    public static async Task<ExtractionResult> RunAsync(string outputPath, Func<string, Task<ProcessResult>> run,
        Func<ProcessResult, string?> failure, Func<ProcessResult, string> successMessage, string toolName)
    {
        if (File.Exists(outputPath))
            return new ExtractionResult(ExtractionStatus.Skipped, outputPath, "Ya existe; no se sobrescribe.");

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = TempPathFor(outputPath);
        try
        {
            var result = await run(temp).ConfigureAwait(false);
            if (failure(result) is { } error)
                return new ExtractionResult(ExtractionStatus.Failed, outputPath, error);

            var info = new FileInfo(temp);
            if (!info.Exists)
                return new ExtractionResult(ExtractionStatus.Failed, outputPath, $"{toolName} terminó pero el archivo de salida no existe.");
            if (info.Length == 0)
                return new ExtractionResult(ExtractionStatus.Failed, outputPath, "El archivo extraído está vacío.");

            try
            {
                File.Move(temp, outputPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(outputPath))
            {
                // Otro proceso lo creó mientras tanto: se respeta el suyo.
                return new ExtractionResult(ExtractionStatus.Skipped, outputPath, "Ya existe; no se sobrescribe.");
            }
            return new ExtractionResult(ExtractionStatus.Extracted, outputPath, successMessage(result));
        }
        finally
        {
            TryDelete(temp); // solo se borra el temporal propio, nunca la salida final
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
