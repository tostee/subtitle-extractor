using System.Text;
using SubtitleTool.Core.Srt;

namespace SubtitleTool.Core.Tests;

public class SrtParserTests
{
    private const string Sample =
        "1\r\n00:00:40,416 --> 00:00:41,584\r\n<i>A face...</i>\r\n\r\n" +
        "2\r\n00:00:44,587 --> 00:00:45,880\r\n- Hello.\r\n- Hi there.\r\n\r\n";

    [Fact]
    public void Parses_Crlf()
    {
        var doc = SrtParser.Parse(Sample);

        Assert.Equal(2, doc.Blocks.Count);
        var b = doc.Blocks[1];
        Assert.Equal(2, b.Index);
        Assert.Equal("00:00:44,587 --> 00:00:45,880", b.TimingLine);
        Assert.Equal(new TimeSpan(0, 0, 0, 44, 587), b.Start);
        Assert.Equal(new TimeSpan(0, 0, 0, 45, 880), b.End);
        Assert.Equal(["- Hello.", "- Hi there."], b.Lines);
    }

    [Fact]
    public void Lf_And_Cr_GiveSameResultAsCrlf()
    {
        var crlf = SrtParser.Parse(Sample);
        var lf = SrtParser.Parse(Sample.Replace("\r\n", "\n"));
        var cr = SrtParser.Parse(Sample.Replace("\r\n", "\r"));

        Assert.Equal(SrtWriter.Write(crlf), SrtWriter.Write(lf));
        Assert.Equal(SrtWriter.Write(crlf), SrtWriter.Write(cr));
    }

    [Fact]
    public void Tolerates_Bom_InStringAndFile()
    {
        var doc = SrtParser.Parse("﻿" + Sample);
        Assert.Equal(1, doc.Blocks[0].Index);

        var path = Path.Combine(Path.GetTempPath(), $"subtool_bom_{Guid.NewGuid():N}.srt");
        try
        {
            File.WriteAllText(path, Sample, new UTF8Encoding(true));
            var fromFile = SrtParser.ParseFile(path);
            Assert.Equal(2, fromFile.Blocks.Count);
            Assert.Equal(1, fromFile.Blocks[0].Index);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Tolerates_MultipleBlankLines_And_TrailingSpaces()
    {
        var text = "\n\n1  \n00:00:01,000 --> 00:00:02,000   \nHello\n\n\n\n   \n2\n00:00:03,000 --> 00:00:04,000\nWorld\n\n\n";

        var doc = SrtParser.Parse(text);

        Assert.Equal(2, doc.Blocks.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), doc.Blocks[0].End);
        Assert.Equal(["World"], doc.Blocks[1].Lines);
    }

    [Fact]
    public void Tolerates_EmptyBlocks()
    {
        var text = "1\n00:00:01,000 --> 00:00:02,000\n\n2\n00:00:03,000 --> 00:00:04,000\nText\n\n" +
                   "3\n00:00:05,000 --> 00:00:06,000\n4\n00:00:07,000 --> 00:00:08,000\nMore\n";

        var doc = SrtParser.Parse(text);

        Assert.Equal(4, doc.Blocks.Count);
        Assert.Empty(doc.Blocks[0].Lines);
        Assert.True(doc.Blocks[0].IsEmpty);
        Assert.Empty(doc.Blocks[2].Lines);
        Assert.Equal(["More"], doc.Blocks[3].Lines);
    }

    [Fact]
    public void Keeps_PositionAfterTimes_InTimingLine()
    {
        var text = "1\n00:00:01,000 --> 00:00:02,500 X1:100 X2:200 Y1:300 Y2:400\nPositioned\n";

        var b = SrtParser.Parse(text).Blocks[0];

        Assert.Equal("00:00:01,000 --> 00:00:02,500 X1:100 X2:200 Y1:300 Y2:400", b.TimingLine);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), b.End);
    }

    [Fact]
    public void RoundTrip_ParseWriteParse_IsIdentical()
    {
        var text = "﻿1\n00:00:01,000 --> 00:00:02,000 X1:1 X2:2 Y1:3 Y2:4\n<i>Hello</i>\nsecond line\n\n\n" +
                   "2\r\n00:00:03,000 --> 00:00:04,000\r\n\r\n" +
                   "3\r00:01:03,5 --> 01:00:04.250\r{\\an8}Top\r";

        var first = SrtParser.Parse(text);
        var written = SrtWriter.Write(first);
        var second = SrtParser.Parse(written);

        Assert.Equal(first.Blocks.Count, second.Blocks.Count);
        for (var i = 0; i < first.Blocks.Count; i++)
        {
            Assert.Equal(first.Blocks[i].Index, second.Blocks[i].Index);
            Assert.Equal(first.Blocks[i].TimingLine, second.Blocks[i].TimingLine);
            Assert.Equal(first.Blocks[i].Start, second.Blocks[i].Start);
            Assert.Equal(first.Blocks[i].End, second.Blocks[i].End);
            Assert.Equal(first.Blocks[i].Lines, second.Blocks[i].Lines);
        }
        Assert.Equal(written, SrtWriter.Write(second));
        Assert.DoesNotContain("\n", written.Replace("\r\n", ""));
        Assert.Equal(TimeSpan.FromMilliseconds(63_500), first.Blocks[2].Start);
    }

    [Fact]
    public void Writer_WritesUtf8WithoutBom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"subtool_nobom_{Guid.NewGuid():N}.srt");
        try
        {
            SrtWriter.WriteFile(path, SrtParser.Parse("1\n00:00:01,000 --> 00:00:02,000\nAñejo ¿qué?\n"));
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal("1\r\n00:00:01,000 --> 00:00:02,000\r\nAñejo ¿qué?\r\n", Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("1\n00:00:01,000 -> 00:00:02,000\nText\n", 2)]
    [InlineData("1\n00:00:01,000 --> 00:61:02,000\nText\n", 2)]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nText\n\n2\nnot a timing line\nText\n", 6)]
    [InlineData("1\n00:00:01 --> 00:00:02,000\nText\n", 2)]
    public void MalformedTiming_ThrowsWithLineNumber(string text, int expectedLine)
    {
        var ex = Assert.Throws<SrtParseException>(() => SrtParser.Parse(text));

        Assert.Equal(expectedLine, ex.LineNumber);
        Assert.Contains($"Línea {expectedLine}", ex.Message);
    }

    [Fact]
    public void NonNumericIndex_ThrowsWithLineNumber()
    {
        var ex = Assert.Throws<SrtParseException>(() => SrtParser.Parse("\n\nabc\n00:00:01,000 --> 00:00:02,000\n"));

        Assert.Equal(3, ex.LineNumber);
    }
}
