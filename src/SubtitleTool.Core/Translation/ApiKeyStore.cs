using System.Security.Cryptography;
using System.Text;

namespace SubtitleTool.Core.Translation;

public enum ApiKeySource
{
    None,
    EnvironmentVariable,
    EncryptedFile,
}

public sealed record ApiKeyInfo(ApiKeySource Source, string? Key)
{
    /// <summary>Últimos 4 caracteres (nunca la clave completa).</summary>
    public string? Last4 => Key is { Length: >= 4 } k ? k[^4..] : null;
}

/// <summary>
/// Clave de API: primero la variable <c>ANTHROPIC_API_KEY</c>; si no, un archivo cifrado con DPAPI (usuario actual).
/// La clave nunca se guarda en texto plano.
/// </summary>
public sealed class ApiKeyStore
{
    public const string EnvironmentVariable = "ANTHROPIC_API_KEY";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SubtitleTool.ApiKey.v1");

    private readonly string _file;
    private readonly Func<string, string?> _getEnv;

    public ApiKeyStore(string? file = null, Func<string, string?>? getEnv = null)
    {
        _file = file ?? AppPaths.ApiKeyFile;
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
    }

    public string FilePath => _file;

    public ApiKeyInfo Resolve()
    {
        var env = _getEnv(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
            return new ApiKeyInfo(ApiKeySource.EnvironmentVariable, env.Trim());

        var stored = ReadFile();
        return stored is null ? new ApiKeyInfo(ApiKeySource.None, null) : new ApiKeyInfo(ApiKeySource.EncryptedFile, stored);
    }

    public void Save(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("La clave está vacía.", nameof(key));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("El cifrado con DPAPI solo está disponible en Windows.");
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllBytes(_file, data);
    }

    /// <summary>Borra el archivo cifrado. Devuelve false si no existía.</summary>
    public bool Clear()
    {
        if (!File.Exists(_file))
            return false;
        File.Delete(_file);
        return true;
    }

    private string? ReadFile()
    {
        if (!File.Exists(_file) || !OperatingSystem.IsWindows())
            return null;
        try
        {
            var data = ProtectedData.Unprotect(File.ReadAllBytes(_file), Entropy, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(data);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
