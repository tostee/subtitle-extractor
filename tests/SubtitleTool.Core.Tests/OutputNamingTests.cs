using SubtitleTool.Core.Mkv;

namespace SubtitleTool.Core.Tests;

public class OutputNamingTests
{
    private const string Video = @"D:\Pelis\The Movie (2026) 1080p.x265.mkv";

    [Theory]
    [InlineData("S_TEXT/UTF8", SubtitleKind.Normal, @"D:\Pelis\The Movie (2026) 1080p.x265.en.srt")]
    [InlineData("S_TEXT/UTF8", SubtitleKind.Forced, @"D:\Pelis\The Movie (2026) 1080p.x265.en.srt")]
    [InlineData("S_TEXT/UTF8", SubtitleKind.Sdh, @"D:\Pelis\The Movie (2026) 1080p.x265.en.sdh.srt")]
    [InlineData("S_TEXT/ASS", SubtitleKind.Normal, @"D:\Pelis\The Movie (2026) 1080p.x265.en.ass")]
    [InlineData("S_TEXT/SSA", SubtitleKind.Normal, @"D:\Pelis\The Movie (2026) 1080p.x265.en.ass")]
    [InlineData("S_TEXT/WEBVTT", SubtitleKind.Normal, @"D:\Pelis\The Movie (2026) 1080p.x265.en.vtt")]
    public void EnglishPath_ByCodecAndKind(string codecId, SubtitleKind kind, string expected)
    {
        Assert.Equal(expected, OutputNaming.EnglishPath(Video, codecId, kind));
    }

    [Fact]
    public void EnglishPath_WithOutputDirectory()
    {
        Assert.Equal(@"E:\Subs\The Movie (2026) 1080p.x265.en.srt",
            OutputNaming.EnglishPath(Video, "S_TEXT/UTF8", SubtitleKind.Normal, @"E:\Subs"));
    }

    [Fact]
    public void SpanishPaths()
    {
        Assert.Equal(@"D:\Pelis\The Movie (2026) 1080p.x265.es.srt", OutputNaming.SpanishPath(Video, SubtitleKind.Normal));
        Assert.Equal(@"D:\Pelis\The Movie (2026) 1080p.x265.es.sdh.srt", OutputNaming.SpanishPath(Video, SubtitleKind.Sdh));
    }

    [Fact]
    public void ImageCodec_Throws()
    {
        Assert.Throws<ArgumentException>(() => OutputNaming.EnglishPath(Video, "S_HDMV/PGS", SubtitleKind.Normal));
    }
}
