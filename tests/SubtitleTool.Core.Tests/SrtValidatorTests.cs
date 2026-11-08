using SubtitleTool.Core.Srt;

namespace SubtitleTool.Core.Tests;

public class SrtValidatorTests
{
    private const string Original =
        "1\n00:00:01,000 --> 00:00:02,000\n<i>A face...</i>\n\n" +
        "2\n00:00:03,000 --> 00:00:04,000\n- Hello.\n- Hi.\n\n" +
        "3\n00:00:05,000 --> 00:00:06,000\nWe sail at dawn.\n";

    private const string Translated =
        "1\n00:00:01,000 --> 00:00:02,000\n<i>Un rostro...</i>\n\n" +
        "2\n00:00:03,000 --> 00:00:04,000\n- Hola.\n- Qué tal.\n\n" +
        "3\n00:00:05,000 --> 00:00:06,000\nZarpamos al amanecer.\n";

    private static ValidationReport Run(string original, string translated) =>
        SrtValidator.Validate(SrtParser.Parse(original), SrtParser.Parse(translated));

    [Fact]
    public void SameTimings_NoErrorsNoWarnings()
    {
        var r = Run(Original, Translated);

        Assert.False(r.HasErrors);
        Assert.Equal(0, r.WarningCount);
        Assert.Equal(3, r.OriginalBlocks);
        Assert.Equal(3, r.TranslatedBlocks);
    }

    [Fact]
    public void SameTimings_DifferentTimingLineFormatting_NoError()
    {
        // Mismos Start/End aunque la línea se escriba distinto (punto en vez de coma, espacios).
        var r = Run(Original, Translated.Replace("00:00:05,000 --> 00:00:06,000", "00:00:05.000  -->  00:00:06,000"));

        Assert.False(r.HasErrors);
    }

    [Fact]
    public void ChangedTiming_ReportsErrorOnThatBlock()
    {
        var r = Run(Original, Translated.Replace("00:00:03,000 --> 00:00:04,000", "00:00:03,000 --> 00:00:04,100"));

        var e = Assert.Single(r.Errors);
        Assert.Equal(IssueKind.TimingMismatch, e.Kind);
        Assert.Equal(2, e.BlockIndex);
    }

    [Fact]
    public void MissingBlock_ReportsError()
    {
        var translated = Translated[..Translated.IndexOf("3\n00:00:05", StringComparison.Ordinal)];

        var r = Run(Original, translated);

        Assert.True(r.HasErrors);
        Assert.Contains(r.Errors, e => e.Kind == IssueKind.BlockCountMismatch);
    }

    [Fact]
    public void DifferentIndex_ReportsError()
    {
        var r = Run(Original, Translated.Replace("3\n00:00:05", "4\n00:00:05"));

        var e = Assert.Single(r.Errors);
        Assert.Equal(IssueKind.IndexMismatch, e.Kind);
    }

    [Fact]
    public void EmptyTranslatedBlock_ReportsError()
    {
        var r = Run(Original, Translated.Replace("Zarpamos al amanecer.\n", "\n"));

        var e = Assert.Single(r.Errors);
        Assert.Equal(IssueKind.EmptyTranslation, e.Kind);
        Assert.Equal(3, e.BlockIndex);
    }

    [Fact]
    public void LostItalicTag_ReportsWarning()
    {
        var r = Run(Original, Translated.Replace("<i>Un rostro...</i>", "Un rostro..."));

        Assert.False(r.HasErrors);
        var w = Assert.Single(r.Warnings);
        Assert.Equal(IssueKind.TagMismatch, w.Kind);
        Assert.Equal(1, w.BlockIndex);
    }

    [Fact]
    public void LongLine_ReportsWarning()
    {
        var r = Run(Original, Translated.Replace("Zarpamos al amanecer.", "Zarpamos al amanecer con todos los barcos de Ítaca."));

        var w = Assert.Single(r.Warnings);
        Assert.Equal(IssueKind.LineTooLong, w.Kind);
        Assert.Equal(3, w.BlockIndex);
    }

    [Fact]
    public void LineLength_IgnoresTags()
    {
        // 42 caracteres visibles exactos dentro de <i>: no debe avisar.
        var text42 = new string('a', 42);
        var r = Run(Original, Translated.Replace("<i>Un rostro...</i>", $"<i>{text42}</i>"));

        Assert.Equal(0, r.WarningCount);
    }

    [Fact]
    public void TooManyLines_And_DashMismatch_ReportWarnings()
    {
        var r = Run(Original, Translated.Replace("- Hola.\n- Qué tal.", "Hola.\nQué tal.\nOtra."));

        Assert.Contains(r.Warnings, w => w.Kind == IssueKind.TooManyLines && w.BlockIndex == 2);
        Assert.Contains(r.Warnings, w => w.Kind == IssueKind.DialogueDashMismatch && w.BlockIndex == 2);
        Assert.False(r.HasErrors);
    }
}
