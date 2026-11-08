using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SubtitleTool.Core.Srt;

public sealed class SrtParseException : FormatException
{
    public int LineNumber { get; }

    public SrtParseException(int lineNumber, string message)
        : base($"Línea {lineNumber}: {message}") => LineNumber = lineNumber;
}

/// <summary>
/// Parser de SRT tolerante: BOM, CRLF/LF/CR, varias líneas en blanco seguidas, espacios finales,
/// ajustes de posición tras los tiempos (<c>X1:... Y2:...</c>) y bloques sin texto.
/// Los errores indican el número de línea.
/// </summary>
public static partial class SrtParser
{
    [GeneratedRegex(@"^\s*(?<s>\d+:\d{1,2}:\d{1,2}[,.]\d{1,3})\s*-->\s*(?<e>\d+:\d{1,2}:\d{1,2}[,.]\d{1,3})(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TimingRegex();

    public static SrtDocument ParseFile(string path)
    {
        // UTF-8 por defecto; si hay BOM (UTF-8/UTF-16) se respeta.
        var text = File.ReadAllText(path, new UTF8Encoding(false));
        return Parse(text);
    }

    public static async Task<SrtDocument> ParseFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return Parse(text);
    }

    public static SrtDocument Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];

        var lines = SplitLines(text);
        var doc = new SrtDocument();
        var i = 0;

        while (i < lines.Count)
        {
            if (IsBlank(lines[i])) { i++; continue; }

            var indexLineNo = i + 1;
            if (!int.TryParse(lines[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                throw new SrtParseException(indexLineNo, $"se esperaba el número de bloque y se encontró \"{Shorten(lines[i])}\".");
            i++;

            if (i >= lines.Count)
                throw new SrtParseException(indexLineNo, $"el bloque {index} no tiene línea de tiempos.");

            var timingLine = lines[i];
            var timingLineNo = i + 1;
            if (!TryParseTiming(timingLine, out var start, out var end, out var error))
                throw new SrtParseException(timingLineNo, $"tiempos mal formados en el bloque {index}: {error} (\"{Shorten(timingLine)}\").");
            i++;

            var block = new SrtBlock
            {
                Index = index,
                TimingLine = timingLine,
                Start = start,
                End = end,
                SourceLine = indexLineNo,
            };

            while (i < lines.Count && !IsBlank(lines[i]))
            {
                // Tolerancia: bloque vacío sin línea en blanco de separación (número seguido de línea de tiempos).
                if (IsNewBlockStart(lines, i))
                    break;
                block.Lines.Add(lines[i]);
                i++;
            }

            doc.Blocks.Add(block);
        }

        return doc;
    }

    public static bool TryParseTiming(string line, out TimeSpan start, out TimeSpan end, out string error)
    {
        start = end = default;
        var m = TimingRegex().Match(line);
        if (!m.Success)
        {
            error = "se esperaba \"hh:mm:ss,mmm --> hh:mm:ss,mmm\"";
            return false;
        }
        if (!TryParseTimestamp(m.Groups["s"].Value, out start))
        {
            error = $"tiempo de inicio inválido \"{m.Groups["s"].Value}\"";
            return false;
        }
        if (!TryParseTimestamp(m.Groups["e"].Value, out end))
        {
            error = $"tiempo de fin inválido \"{m.Groups["e"].Value}\"";
            return false;
        }
        // Lo que sigue a los tiempos debe ser vacío o ajustes de posición separados por espacio.
        var rest = m.Groups["rest"].Value;
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]))
        {
            error = $"texto inesperado tras el tiempo de fin \"{rest.Trim()}\"";
            return false;
        }
        error = "";
        return true;
    }

    public static bool TryParseTimestamp(string value, out TimeSpan result)
    {
        result = default;
        var parts = value.Split(':');
        if (parts.Length != 3)
            return false;
        var secParts = parts[2].Split(',', '.');
        if (secParts.Length != 2)
            return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ||
            !int.TryParse(secParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ||
            !int.TryParse(secParts[1].PadRight(3, '0'), NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
            return false;
        if (m > 59 || s > 59 || ms > 999)
            return false;
        result = new TimeSpan(0, h, m, s, ms);
        return true;
    }

    private static bool IsNewBlockStart(List<string> lines, int i) =>
        i + 1 < lines.Count
        && int.TryParse(lines[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)
        && TimingRegex().IsMatch(lines[i + 1]);

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    private static List<string> SplitLines(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                result.Add(sb.ToString());
                sb.Clear();
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
            }
            else if (c == '\n')
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0)
            result.Add(sb.ToString());
        return result;
    }

    private static string Shorten(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
