using System.Text.RegularExpressions;

namespace SubtitleTool.Core.Translation;

/// <summary>
/// Rutas derivadas de un subtítulo en inglés: <c>X.en.srt</c> → <c>X.es.srt</c>, <c>X.en.sdh.srt</c> → <c>X.es.sdh.srt</c>,
/// copia sin idioma <c>X.srt</c> y glosario <c>X.glossary.json</c>.
/// </summary>
public sealed partial record TranslationPaths(string EnglishPath, string SpanishPath, string PlainCopyPath, string GlossaryPath, bool IsSdh)
{
    [GeneratedRegex(@"^(?<base>.+)\.en(?<sdh>\.sdh)?\.srt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishNameRegex();

    public static bool TryFromEnglish(string englishPath, out TranslationPaths? paths)
    {
        paths = null;
        var full = Path.GetFullPath(englishPath);
        var dir = Path.GetDirectoryName(full) ?? ".";
        var m = EnglishNameRegex().Match(Path.GetFileName(full));
        if (!m.Success)
            return false;
        var baseName = Path.Combine(dir, m.Groups["base"].Value);
        var sdh = m.Groups["sdh"].Success;
        paths = new TranslationPaths(
            full,
            baseName + ".es" + (sdh ? ".sdh" : "") + ".srt",
            baseName + ".srt",
            baseName + ".glossary.json",
            sdh);
        return true;
    }

    public static TranslationPaths FromEnglish(string englishPath) =>
        TryFromEnglish(englishPath, out var p)
            ? p!
            : throw new ArgumentException(
                $"El archivo debe terminar en .en.srt o .en.sdh.srt: {Path.GetFileName(englishPath)}", nameof(englishPath));
}
