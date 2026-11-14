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
    /// <summary>Variables de entorno que se añaden o sustituyen.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();
    /// <summary>Se llama con cada línea de stdout según llega (procesos largos con progreso). Desde un hilo de fondo.</summary>
    public Action<string>? OnStandardOutputLine { get; init; }
    /// <summary>Se llama con cada línea de stderr según llega. Desde un hilo de fondo.</summary>
    public Action<string>? OnStandardErrorLine { get; init; }
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
    // Procesos en marcha: si la app se cierra con uno largo abierto (doblaje, instalación), se termina con ella
    // en vez de quedarse huérfano ocupando la GPU o descargando.
    private static readonly HashSet<Process> Running = [];

    static ProcessRunner()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Process[] alive;
            lock (Running)
                alive = [.. Running];
            foreach (var p in alive)
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Ya terminó o no se puede: se sigue cerrando.
                }
            }
        };
    }

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
        foreach (var (name, value) in options.EnvironmentVariables)
            psi.Environment[name] = value;
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"No se pudo iniciar el proceso '{fileName}'.");
        lock (Running)
            Running.Add(process);
        try
        {
            return await WaitAsync(process, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (Running)
                Running.Remove(process);
        }
    }

    private static async Task<ProcessResult> WaitAsync(Process process, ProcessRunOptions options, CancellationToken cancellationToken)
    {

        var stdoutTask = ReadAllAsync(process.StandardOutput, options.OnStandardOutputLine);
        var stderrTask = ReadAllAsync(process.StandardError, options.OnStandardErrorLine);
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

    /// <summary>Sin callback: todo de una vez. Con callback: línea a línea (se avisa de cada una) y se devuelve todo igual.</summary>
    private static async Task<string> ReadAllAsync(StreamReader reader, Action<string>? onLine)
    {
        if (onLine is null)
            return await reader.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
        var sb = new StringBuilder();
        while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            sb.AppendLine(line);
            try
            {
                onLine(line);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Un error al mostrar el progreso no debe cortar la lectura (el proceso se quedaría bloqueado).
            }
        }
        return sb.ToString();
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
