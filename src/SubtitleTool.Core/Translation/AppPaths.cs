namespace SubtitleTool.Core.Translation;

/// <summary>Carpetas de datos de la aplicación: <c>%LOCALAPPDATA%\SubtitleTool</c> (jobs, logs, clave).</summary>
public static class AppPaths
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SubtitleTool");

    public static string Jobs => Path.Combine(Root, "jobs");
    public static string Logs => Path.Combine(Root, "logs");
    public static string ApiKeyFile => Path.Combine(Root, "apikey.bin");
    /// <summary>Ajustes de la interfaz gráfica (nunca contiene la clave de API).</summary>
    public static string SettingsFile => Path.Combine(Root, "settings.json");
}
