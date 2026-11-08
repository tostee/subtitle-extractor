using SubtitleTool.App.Models;
using SubtitleTool.App.ViewModels;
using SubtitleTool.Core.Library;
using SubtitleTool.Core.Media;
using SubtitleTool.Core.Review;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Updates;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.App.Services;

/// <summary>Lectura y guardado de los ajustes.</summary>
public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

/// <summary>Búsqueda e inspección de videos (LibraryScanner + MKVToolNix para .mkv y FFmpeg para .mp4).</summary>
public interface ILibraryService
{
    /// <summary>Videos (.mkv, .mp4, .m4v) de un archivo o carpeta (recursiva).</summary>
    IReadOnlyList<string> FindVideos(string path);

    /// <remarks>Si falta la herramienta (MKVToolNix para un .mkv, FFmpeg para un .mp4) no lanza: se devuelve con
    /// <see cref="VideoStatus.Error"/> (y <see cref="VideoStatus.MkvToolNixMissing"/> si es MKVToolNix).</remarks>
    Task<VideoStatus> ScanAsync(string videoPath, MediaToolDirectories tools, CancellationToken ct = default);

    /// <summary>Carpeta de MKVToolNix encontrada (la preferida si existe, si no autodetección), o null.</summary>
    string? DetectMkvToolNix(string? preferred = null);

    /// <summary>Carpeta de FFmpeg encontrada (la preferida si existe, si no autodetección), o null.</summary>
    string? DetectFfmpeg(string? preferred = null);
}

/// <summary>Revisión de traducciones (Core: <see cref="ReviewService"/>).</summary>
public interface IReviewService
{
    bool HasDraft(string? englishPath);

    /// <exception cref="ReviewException">No hay nada que revisar o no encaja bloque a bloque.</exception>
    Task<ReviewSession> OpenAsync(string englishPath, CancellationToken ct = default);

    Task SaveDraftAsync(ReviewSession session, CancellationToken ct = default);

    /// <exception cref="ReviewException">Errores de validación o el destino ya existe.</exception>
    Task<ReviewSaveResult> SaveAsync(ReviewSession session, CancellationToken ct = default);

    void Discard(string englishPath);
}

/// <summary>Actualizaciones desde GitHub Releases.</summary>
public interface IUpdateService
{
    /// <summary>Versión instalada, p. ej. "1.0.0".</summary>
    string CurrentVersion { get; }

    /// <summary>false en una compilación de desarrollo (sin instalador): solo se puede abrir la página de descargas.</summary>
    bool CanSelfUpdate { get; }

    /// <exception cref="UpdateException">Sin conexión o GitHub no responde.</exception>
    Task<ReleaseInfo?> FindNewerAsync(CancellationToken ct = default);

    /// <exception cref="UpdateException">Descarga fallida o huella SHA-256 incorrecta.</exception>
    Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken ct = default);

    /// <summary>Lanza el instalador en modo silencioso y cierra la app (el instalador la vuelve a abrir).</summary>
    /// <exception cref="UpdateException">No se pudo iniciar el instalador (p. ej. se rechazó el permiso de administrador).</exception>
    void InstallAndExit(string installerPath);
}

/// <summary>Registro en archivo de la actividad y los errores (<c>%LOCALAPPDATA%\SubtitleTool\logs</c>).</summary>
public interface IActivityLog
{
    string FolderPath { get; }
    void Write(string message);
    void WriteException(string context, Exception exception);
}

/// <summary>Extrae y traduce un video (Core: <see cref="VideoProcessor"/>).</summary>
public interface IVideoProcessingService
{
    Task<VideoOutcome> ProcessAsync(VideoStatus status, AppSettings settings, IProgress<VideoProgress> progress, CancellationToken ct);
}

/// <summary>Glosario de un video (<c>&lt;video&gt;.glossary.json</c>).</summary>
public interface IGlossaryService
{
    /// <summary>null si el archivo no existe.</summary>
    /// <exception cref="InvalidDataException">Si el archivo no es un glosario JSON válido.</exception>
    Glossary? Load(string glossaryPath);

    void Save(string glossaryPath, Glossary glossary);

    /// <summary>Genera de nuevo el glosario con Claude (backend de los ajustes) y lo guarda.</summary>
    Task<Glossary> RegenerateAsync(string englishPath, AppSettings settings, CancellationToken ct);
}

public interface IDoctorService
{
    Task<IReadOnlyList<DoctorCheck>> RunAsync(MediaToolDirectories tools, CancellationToken ct = default);
}

public interface IApiKeyService
{
    ApiKeyInfo Resolve();
    void Save(string key);
    bool Clear();
}

/// <summary>Diálogos y acciones del sistema (en pruebas se sustituye por uno falso).</summary>
public interface IDialogService
{
    string? PickFolder(string title);
    IReadOnlyList<string> PickVideoFiles();
    void ShowInfo(string message, string title = "Subtítulos");
    void ShowWarning(string message, string title = "Subtítulos");
    void ShowError(string message, string title = "Subtítulos");
    bool Confirm(string message, string title = "Subtítulos");

    /// <summary>Cambios sin guardar al cerrar: true = guardar, false = no guardar, null = cancelar (seguir en la ventana).</summary>
    bool? AskSaveChanges(string message, string title = "Subtítulos");

    void OpenFolder(string path);
    void OpenFile(string path);
    void OpenUrl(string url);

    /// <summary>Texto de solo lectura. <paramref name="prose"/>: texto corrido (con ajuste de línea), p. ej. notas de versión.</summary>
    void ShowText(string title, string text, bool prose = false);
    bool ShowSettings(SettingsViewModel viewModel);
    void ShowGlossaryEditor(GlossaryEditorViewModel viewModel);
    void ShowReview(ReviewViewModel viewModel);
    void ShowSummary(IReadOnlyList<SummaryRow> rows);
}
