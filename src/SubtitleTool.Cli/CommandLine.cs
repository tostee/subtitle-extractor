namespace SubtitleTool.Cli;

public sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}

/// <summary>Parser de argumentos sencillo: posicionales + opciones <c>--nombre valor</c>.</summary>
public sealed class ParsedArgs
{
    public List<string> Positionals { get; } = [];
    public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="knownFlags">Opciones booleanas sin valor (p. ej. <c>--force</c>).</param>
    public static ParsedArgs Parse(IEnumerable<string> args, IReadOnlySet<string> knownOptions, IReadOnlySet<string>? knownFlags = null)
    {
        var result = new ParsedArgs();
        using var e = args.GetEnumerator();
        while (e.MoveNext())
        {
            var a = e.Current;
            if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
            {
                var name = a[2..];
                string? value = null;
                var eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                }
                if (value is null && knownFlags is not null && knownFlags.Contains(name))
                {
                    result.Flags.Add(name);
                    continue;
                }
                if (!knownOptions.Contains(name))
                    throw new UsageException($"Opción desconocida: --{name}");
                if (value is null)
                {
                    if (!e.MoveNext())
                        throw new UsageException($"Falta el valor de la opción --{name}");
                    value = e.Current;
                }
                result.Options[name] = value;
            }
            else
            {
                result.Positionals.Add(a);
            }
        }
        return result;
    }

    public string? Get(string name) => Options.TryGetValue(name, out var v) ? v : null;

    public bool Has(string flag) => Flags.Contains(flag);

    public void RequirePositionals(int count, string usage)
    {
        if (Positionals.Count != count)
            throw new UsageException($"Número de argumentos incorrecto.\nUso: {usage}");
    }
}
