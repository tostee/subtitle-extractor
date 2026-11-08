using System.Text;
using System.Text.RegularExpressions;

namespace SubtitleTool.Core.Srt;

public enum IssueSeverity
{
    Error,
    Warning,
}

public enum IssueKind
{
    // Errores
    BlockCountMismatch,
    IndexMismatch,
    TimingMismatch,
    EmptyTranslation,
    // Advertencias
    TagMismatch,
    TooManyLines,
    LineTooLong,
    DialogueDashMismatch,
}

public sealed record ValidationIssue(IssueSeverity Severity, IssueKind Kind, int? BlockIndex, string Message);

public sealed class ValidationReport
{
    public int OriginalBlocks { get; init; }
    public int TranslatedBlocks { get; init; }
    public List<ValidationIssue> Issues { get; } = [];

    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == IssueSeverity.Error);
    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == IssueSeverity.Warning);
    public int ErrorCount => Errors.Count();
    public int WarningCount => Warnings.Count();
    public bool HasErrors => ErrorCount > 0;

    /// <summary>Conteo de incidencias por tipo.</summary>
    public IReadOnlyDictionary<IssueKind, int> CountsByKind =>
        Issues.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>Compara un SRT original con su traducción y detecta errores estructurales y advertencias de estilo.</summary>
public static partial class SrtValidator
{
    public const int MaxLines = 2;
    public const int MaxLineLength = 42;

    [GeneratedRegex(@"</?[ibu]>|<font\b[^>]*>|</font>|\{\\[^}]*\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"<[^>]*>|\{\\[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex AnyTagRegex();

    public static ValidationReport Validate(SrtDocument original, SrtDocument translated)
    {
        var report = new ValidationReport
        {
            OriginalBlocks = original.Blocks.Count,
            TranslatedBlocks = translated.Blocks.Count,
        };

        if (original.Blocks.Count != translated.Blocks.Count)
        {
            report.Issues.Add(new(IssueSeverity.Error, IssueKind.BlockCountMismatch, null,
                $"Número de bloques distinto: original {original.Blocks.Count}, traducido {translated.Blocks.Count}."));
        }

        var n = Math.Min(original.Blocks.Count, translated.Blocks.Count);
        for (var p = 0; p < n; p++)
        {
            var o = original.Blocks[p];
            var t = translated.Blocks[p];
            var pos = p + 1;

            if (o.Index != t.Index)
            {
                report.Issues.Add(new(IssueSeverity.Error, IssueKind.IndexMismatch, o.Index,
                    $"Posición {pos}: índice distinto (original {o.Index}, traducido {t.Index})."));
            }

            if (o.Start != t.Start || o.End != t.End)
            {
                report.Issues.Add(new(IssueSeverity.Error, IssueKind.TimingMismatch, o.Index,
                    $"Bloque {o.Index}: tiempos distintos (original \"{o.TimingLine.Trim()}\", traducido \"{t.TimingLine.Trim()}\")."));
            }

            if (t.IsEmpty && !o.IsEmpty)
            {
                report.Issues.Add(new(IssueSeverity.Error, IssueKind.EmptyTranslation, o.Index,
                    $"Bloque {o.Index}: la traducción está vacía y el original no."));
            }

            CheckWarnings(report, o, t);
        }

        return report;
    }

    private static void CheckWarnings(ValidationReport report, SrtBlock o, SrtBlock t)
    {
        var oTags = TagMultiset(o.Lines);
        var tTags = TagMultiset(t.Lines);
        if (!SameMultiset(oTags, tTags))
        {
            report.Issues.Add(new(IssueSeverity.Warning, IssueKind.TagMismatch, o.Index,
                $"Bloque {o.Index}: etiquetas distintas (original [{FormatTags(oTags)}], traducido [{FormatTags(tTags)}])."));
        }

        var textLines = t.Lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (textLines.Count > MaxLines)
        {
            report.Issues.Add(new(IssueSeverity.Warning, IssueKind.TooManyLines, o.Index,
                $"Bloque {o.Index}: {textLines.Count} líneas (máximo recomendado {MaxLines})."));
        }

        foreach (var line in textLines)
        {
            var len = VisibleLength(line);
            if (len > MaxLineLength)
            {
                report.Issues.Add(new(IssueSeverity.Warning, IssueKind.LineTooLong, o.Index,
                    $"Bloque {o.Index}: línea de {len} caracteres (máximo {MaxLineLength}): \"{StripTags(line).Trim()}\"."));
            }
        }

        var oDash = HasDialogueDash(o.Lines);
        var tDash = HasDialogueDash(t.Lines);
        if (oDash != tDash)
        {
            report.Issues.Add(new(IssueSeverity.Warning, IssueKind.DialogueDashMismatch, o.Index,
                $"Bloque {o.Index}: guion de diálogo {(oDash ? "en el original pero no en la traducción" : "en la traducción pero no en el original")}."));
        }
    }

    /// <summary>Texto sin etiquetas HTML ni overrides ASS.</summary>
    public static string StripTags(string line) => AnyTagRegex().Replace(line, "");

    /// <summary>Longitud visible: sin etiquetas, sin espacios finales, contando caracteres de texto (grafemas).</summary>
    public static int VisibleLength(string line)
    {
        var text = StripTags(line).TrimEnd().Normalize(NormalizationForm.FormC);
        return new System.Globalization.StringInfo(text).LengthInTextElements;
    }

    public static Dictionary<string, int> TagMultiset(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            foreach (Match m in TagRegex().Matches(line))
            {
                var key = m.Value.ToLowerInvariant();
                result[key] = result.TryGetValue(key, out var c) ? c + 1 : 1;
            }
        }
        return result;
    }

    private static bool SameMultiset(Dictionary<string, int> a, Dictionary<string, int> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var c) && c == kv.Value);

    private static string FormatTags(Dictionary<string, int> tags) =>
        string.Join(" ", tags.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value == 1 ? kv.Key : $"{kv.Key}×{kv.Value}"));

    private static bool HasDialogueDash(IEnumerable<string> lines) =>
        lines.Any(l => StripTags(l).TrimStart().StartsWith('-'));
}
