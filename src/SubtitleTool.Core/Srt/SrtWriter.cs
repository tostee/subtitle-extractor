using System.Text;

namespace SubtitleTool.Core.Srt;

/// <summary>Escribe SRT en UTF-8 sin BOM, con CRLF y una línea en blanco entre bloques.</summary>
public static class SrtWriter
{
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static string Write(SrtDocument document)
    {
        var sb = new StringBuilder();
        for (var b = 0; b < document.Blocks.Count; b++)
        {
            var block = document.Blocks[b];
            if (b > 0)
                sb.Append("\r\n");
            sb.Append(block.Index).Append("\r\n");
            sb.Append(block.TimingLine).Append("\r\n");
            foreach (var line in block.Lines)
                sb.Append(line).Append("\r\n");
        }
        return sb.ToString();
    }

    public static void WriteFile(string path, SrtDocument document) =>
        File.WriteAllText(path, Write(document), Utf8NoBom);

    public static Task WriteFileAsync(string path, SrtDocument document, CancellationToken cancellationToken = default) =>
        File.WriteAllTextAsync(path, Write(document), Utf8NoBom, cancellationToken);
}
