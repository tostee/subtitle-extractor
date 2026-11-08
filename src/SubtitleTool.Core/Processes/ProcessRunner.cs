using System.Diagnostics;
using System.Text;

namespace SubtitleTool.Core.Processes;

/// <summary>Resultado de ejecutar un proceso externo.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Opciones adicionales: texto para stdin (UTF-8, se cierra al terminar), carpeta de trabajo y variables de entorno a quitar.</summary>
public sealed record ProcessRunOptions
{
    public string? StandardInput { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyList<string> RemoveEnvironmentVariables { get; init; } = [];
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken = default);

    Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, ProcessRunOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lanza procesos usando <see cref="ProcessStartInfo.ArgumentList"/> (cada argumento se pasa tal cual,
/// sin concatenar ni escapar a mano). stdout/stderr se leen en UTF-8. Si se cancela, se mata el árbol de procesos.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken = default) =>
        RunAsync(fileName, arguments, new ProcessRunOptions(), cancellationToken);

    public async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, ProcessRunOptions options, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            RedirectStandardInput = options.StandardInput is not null,
        };
        if (options.StandardInput is not null)
            psi.StandardInputEncoding = new UTF8Encoding(false);
        if (options.WorkingDirectory is not null)
            psi.WorkingDirectory = options.WorkingDirectory;
        foreach (var name in options.RemoveEnvironmentVariables)
            psi.Environment.Remove(name);
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"No se pudo iniciar el proceso '{fileName}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdinTask = options.StandardInput is null ? Task.CompletedTask : WriteInputAsync(process, options.StandardInput);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                // Kill no espera: hasta que el proceso termina sigue teniendo abiertos sus archivos de salida
                // (y quien cancela suele querer borrarlos).
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // El proceso ya había terminado.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No se pudo terminar (acceso denegado): se cancela igualmente.
            }
            throw;
        }

        try
        {
            await stdinTask.ConfigureAwait(false);
        }
        catch (IOException)
        {
            // El proceso cerró stdin antes de leerlo todo (p. ej. terminó con error): se informa por su salida.
        }
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static async Task WriteInputAsync(Process process, string input)
    {
        try
        {
            await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            process.StandardInput.Close();
        }
    }
}
