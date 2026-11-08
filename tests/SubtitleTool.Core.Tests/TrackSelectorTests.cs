using SubtitleTool.Core.Mkv;

namespace SubtitleTool.Core.Tests;

public class TrackSelectorTests
{
    private static IReadOnlyList<MkvTrack> Fixture(string name) =>
        MkvInspector.ParseTracks(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void NormalSdhPgs_ChoosesNormalSrt_RealOdysseyCase()
    {
        var sel = TrackSelector.Select(Fixture("odyssey_normal_sdh_pgs.json"));

        Assert.True(sel.Success);
        Assert.Equal(3, sel.Track!.Id);
        Assert.Equal(SubtitleKind.Normal, sel.Kind);
    }

    [Fact]
    public void Inspector_ParsesAllRequestedProperties()
    {
        var tracks = Fixture("odyssey_normal_sdh_pgs.json");
        var sdh = tracks.Single(t => t.Id == 4);

        Assert.Equal("subtitles", sdh.Type);
        Assert.Equal("SubRip/SRT", sdh.Codec);
        Assert.Equal("S_TEXT/UTF8", sdh.CodecId);
        Assert.Equal("eng", sdh.Language);
        Assert.Equal("en-US", sdh.LanguageIetf);
        Assert.True(sdh.HearingImpaired);
        Assert.False(sdh.ForcedTrack);
        Assert.False(sdh.DefaultTrack);
        Assert.Null(tracks.Single(t => t.Id == 3).HearingImpaired);
    }

    [Fact]
    public void OnlySdh_ChoosesSdh()
    {
        var sel = TrackSelector.Select(Fixture("only_sdh.json"));

        Assert.Equal(2, sel.Track!.Id);
        Assert.Equal(SubtitleKind.Sdh, sel.Kind);
    }

    [Fact]
    public void OnlyForced_ChoosesForced()
    {
        var sel = TrackSelector.Select(Fixture("only_forced.json"));

        Assert.Equal(2, sel.Track!.Id);
        Assert.Equal(SubtitleKind.Forced, sel.Kind);
    }

    [Fact]
    public void OnlyPgs_IsImageOnly()
    {
        var sel = TrackSelector.Select(Fixture("only_pgs.json"));

        Assert.False(sel.Success);
        Assert.Equal(SelectionFailure.ImageOnly, sel.Failure);
    }

    [Fact]
    public void NoEnglish_IsNoEnglish()
    {
        var sel = TrackSelector.Select(Fixture("no_english.json"));

        Assert.False(sel.Success);
        Assert.Equal(SelectionFailure.NoEnglish, sel.Failure);
    }

    [Fact]
    public void Tie_ResolvedByDefaultTrack()
    {
        var sel = TrackSelector.Select(Fixture("tie_default.json"));

        Assert.Equal(3, sel.Track!.Id);
        Assert.Equal(SubtitleKind.Normal, sel.Kind);
    }

    [Fact]
    public void Tie_WithoutDefault_ResolvedByLowestId()
    {
        var tracks = Fixture("tie_default.json").Where(t => t.Id != 3);

        var sel = TrackSelector.Select(tracks);

        Assert.Equal(2, sel.Track!.Id);
    }

    [Fact]
    public void LanguageIetfEnUs_WithLanguageUnd_IsEnglish()
    {
        var sel = TrackSelector.Select(Fixture("ietf_und.json"));

        Assert.Equal(1, sel.Track!.Id);
        Assert.Equal("S_TEXT/WEBVTT", sel.Track.CodecId);
    }

    [Theory]
    [InlineData("English SDH", false, null, SubtitleKind.Sdh)]
    [InlineData("English (cc)", false, null, SubtitleKind.Sdh)]
    [InlineData("English", false, true, SubtitleKind.Sdh)]
    [InlineData("FORCED", false, null, SubtitleKind.Forced)]
    [InlineData("English", true, null, SubtitleKind.Forced)]
    [InlineData("English Accent", false, null, SubtitleKind.Normal)]
    [InlineData(null, false, false, SubtitleKind.Normal)]
    public void Classify_ByNameAndFlags(string? name, bool forced, bool? hi, SubtitleKind expected)
    {
        var t = new MkvTrack { Type = "subtitles", CodecId = "S_TEXT/UTF8", Language = "eng", TrackName = name, ForcedTrack = forced, HearingImpaired = hi };

        Assert.Equal(expected, TrackSelector.Classify(t));
    }
}
