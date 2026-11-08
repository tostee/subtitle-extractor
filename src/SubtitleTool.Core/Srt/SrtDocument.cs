namespace SubtitleTool.Core.Srt;

/// <summary>Bloque de un archivo SRT.</summary>
public sealed class SrtBlock
{
    public int Index { get; set; }
    /// <summary>Línea de tiempos exactamente como venía en el archivo (incluye ajustes de posición, p. ej. <c>X1:...</c>).</summary>
    public string TimingLine { get; set; } = "";
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public List<string> Lines { get; set; } = [];

    /// <summary>Número de línea (1-based) del índice en el archivo de origen; 0 si se creó en memoria.</summary>
    public int SourceLine { get; set; }

    public bool IsEmpty => Lines.All(string.IsNullOrWhiteSpace);
}

/// <summary>Documento SRT: lista ordenada de bloques.</summary>
public sealed class SrtDocument
{
    public List<SrtBlock> Blocks { get; } = [];

    public SrtDocument() { }

    public SrtDocument(IEnumerable<SrtBlock> blocks) => Blocks.AddRange(blocks);

    public TimeSpan? FirstStart => Blocks.Count == 0 ? null : Blocks.Min(b => b.Start);
    public TimeSpan? LastEnd => Blocks.Count == 0 ? null : Blocks.Max(b => b.End);
}
