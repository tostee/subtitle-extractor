using SubtitleTool.Core.Media;
using SubtitleTool.Core.Mkv;
using SubtitleTool.Core.Processes;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Workflow;

/// <param name="Text">Texto sin el icono, p. ej. "MKVToolNix: C:\Program Files\MKVToolNix".</param>
/// <param name="Required">false si el punto es informativo y no hace fallar la comprobación (la clave de API).</param>
public sealed record DoctorCheck(bool Ok, string Text, bool Required = true)
{
    public string Icon => Ok ? "✅" : "❌";
    public override string ToString() => $"{Icon} {Text}";
}

/// <summary>Comprobaciones de <c>subtool doctor</c>: MKVToolNix, FFmpeg (solo .mp4), Claude Code (versión y una llamada mínima con haiku) y clave de API.</summary>
public sealed class DoctorService
{
    private readonly MediaToolDirectories _tools;
    private readonly ApiKeyStore _keyStore;

    /// <param name="tools">Carpetas preferidas de MKVToolNix y FFmpeg (ajustes); null = autodetección.</param>
    public DoctorService(MediaToolDirectories? tools = null, ApiKeyStore? keyStore = null)
    {
        _tools = tools ?? MediaToolDirectories.Auto;
        _keyStore = keyStore ?? new ApiKeyStore();
    }

    public static bool AllOk(IEnumerable<DoctorCheck> checks) => checks.Where(c => c.Required).All(c => c.Ok);

    public async Task<IReadOnlyList<DoctorCheck>> RunAsync(CancellationToken ct = default)
    {
        var checks = new List<DoctorCheck>();

        try
        {
            var mkv = MkvToolNixLocator.LocatePreferring(_tools.MkvToolNix);
            checks.Add(new DoctorCheck(true, $"MKVToolNix: {Path.GetDirectoryName(mkv.MkvMerge)}"));
        }
        catch (MkvToolNixNotFoundException ex)
        {
            checks.Add(new DoctorCheck(false, $"MKVToolNix: {ex.Message}"));
        }

        // FFmpeg solo hace falta para .mp4/.m4v: no hace fallar la comprobación.
        try
        {
            var ff = FfmpegLocator.LocatePreferring(_tools.Ffmpeg);
            checks.Add(new DoctorCheck(true, $"FFmpeg (para .mp4): {Path.GetDirectoryName(ff.FFmpeg)}", Required: false));
        }
        catch (FfmpegNotFoundException)
        {
            checks.Add(new DoctorCheck(false,
                $"FFmpeg: no encontrado. Solo hace falta para .mp4/.m4v (winget install {FfmpegLocator.WingetPackage}).", Required: false));
        }

        var exe = ClaudeCodeTranslationClient.ResolveExecutable();
        var claudeOk = false;
        try
        {
            var v = await new ProcessRunner().RunAsync(exe, ["--version"], ct).ConfigureAwait(false);
            claudeOk = v.ExitCode == 0;
            checks.Add(new DoctorCheck(claudeOk, claudeOk
                ? $"Claude Code: {v.StandardOutput.Trim()} ({exe})"
                : $"Claude Code: 'claude --version' terminó con código {v.ExitCode}: {(v.StandardError + v.StandardOutput).Trim()}"));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            checks.Add(new DoctorCheck(false, "Claude Code: 'claude' no está instalado o no está en el PATH (https://claude.com/claude-code)."));
        }

        if (claudeOk)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var client = new ClaudeCodeTranslationClient("claude-haiku-5-5", "low", TimeSpan.FromMinutes(2), executable: exe);
                var o = await client.RunAsync("Responde exactamente: OK", "¿Funciona?", schemaJson: null, cancellationToken: ct).ConfigureAwait(false);
                var ok = o.HasJson && !o.IsError && o.ExitCode == 0;
                checks.Add(new DoctorCheck(ok, ok
                    ? $"Claude Code responde (haiku, {sw.ElapsedMilliseconds} ms): \"{o.ResultText.Trim()}\" — sesión con suscripción OK"
                    : $"Claude Code no respondió bien (código {o.ExitCode}): {(o.ResultText + " " + o.StandardError).Trim()}"));
            }
            catch (TranslationClientException ex)
            {
                checks.Add(new DoctorCheck(false, $"Claude Code: {ex.Message}"));
            }
        }

        var key = _keyStore.Resolve();
        checks.Add(key.Source switch
        {
            ApiKeySource.EnvironmentVariable => new DoctorCheck(true, $"Clave de API: variable {ApiKeyStore.EnvironmentVariable} (…{key.Last4}); solo se usa con --backend api", Required: false),
            ApiKeySource.EncryptedFile => new DoctorCheck(true, $"Clave de API: archivo cifrado (…{key.Last4}); solo se usa con --backend api", Required: false),
            _ => new DoctorCheck(false, "Clave de API: no configurada (no hace falta con --backend claude-code, el predeterminado)", Required: false),
        });

        return checks;
    }
}
