using SubtitleTool.Core.Diagnostics;
using SubtitleTool.Core.Library;
using SubtitleTool.Core.Media;
using SubtitleTool.Core.Mkv;
using SubtitleTool.Core.Processes;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.Core.Tests;

/// <summary>Soporte de .mp4 con FFmpeg, enrutado por formato y registro de errores.</summary>
public sealed class MediaTests : IDisposable
{
    private readonly string _dir;

    public MediaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "subtool-media-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static readonly FfmpegPaths Ff = new(@"C:\ff\ffmpeg.exe", @"C:\ff\ffprobe.exe");
    private static readonly MkvToolNixPaths Mkv = new(@"C:\mkv\mkvmerge.exe", @"C:\mkv\mkvextract.exe");

    /// <summary>Registra las llamadas y, opcionalmente, crea el archivo de salida (último argumento).</summary>
    private sealed class RecordingRunner(Func<string, IReadOnlyList<string>, ProcessResult> handler) : IProcessRunner
    {
        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken = default) =>
            RunAsync(fileName, arguments, new ProcessRunOptions(), cancellationToken);

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, ProcessRunOptions options, CancellationToken cancellationToken = default)
        {
            var args = arguments.ToList();
            Calls.Add((fileName, args));
            return Task.FromResult(handler(fileName, args));
        }
    }

    // ------------------------------------------------------------------ ffprobe

    [Fact]
    public void Ffprobe_RealMp4_ChoosesNormalEnglishOverSdhAndSpanish()
    {
        var tracks = FfprobeInspector.ParseTracks(Fixture("mp4_spa_sdh_eng.json"));

        Assert.Equal(3, tracks.Count(t => t.IsSubtitle));
        var sdh = tracks.Single(t => t.Id == 3);
        Assert.Equal("S_TEXT/UTF8", sdh.CodecId);
        Assert.Equal("eng", sdh.Language);
        Assert.Equal("English SDH", sdh.TrackName); // en .mp4 el título llega como tag "name"
        Assert.Null(tracks.Single(t => t.Id == 2).TrackName); // "SubtitleHandler" no cuenta como nombre
        Assert.Null(tracks.Single(t => t.Id == 0).Language); // "und" → sin idioma

        var sel = TrackSelector.Select(tracks);
        Assert.Equal(4, sel.Track!.Id);
        Assert.Equal(SubtitleKind.Normal, sel.Kind);
        Assert.Equal(@"D:\v\Peli.en.srt", OutputNaming.EnglishPath(@"D:\v\Peli.mp4", sel.Track.CodecId, sel.Kind));
    }

    [Theory]
    [InlineData("mov_text", "S_TEXT/UTF8")]
    [InlineData("subrip", "S_TEXT/UTF8")]
    [InlineData("ass", "S_TEXT/ASS")]
    [InlineData("webvtt", "S_TEXT/WEBVTT")]
    [InlineData("hdmv_pgs_subtitle", "S_HDMV/PGS")]
    [InlineData("dvd_subtitle", "S_VOBSUB")]
    [InlineData("dvb_subtitle", "S_DVBSUB")]
    [InlineData("eia_608", "FFMPEG/eia_608")]
    public void Ffprobe_MapsCodecs(string codec, string expected) => Assert.Equal(expected, FfprobeInspector.MapCodecId(codec));

    [Fact]
    public void Ffprobe_ImageOnlyEnglish_IsImageOnly_AndFlagsAreRead()
    {
        const string json = """
            {"streams":[
              {"index":0,"codec_type":"video","codec_name":"h264"},
              {"index":1,"codec_type":"subtitle","codec_name":"dvd_subtitle","tags":{"language":"eng"},"disposition":{"default":0,"forced":1,"hearing_impaired":1}}
            ]}
            """;
        var tracks = FfprobeInspector.ParseTracks(json);
        var sub = tracks.Single(t => t.IsSubtitle);
        Assert.True(sub.ForcedTrack);
        Assert.True(sub.HearingImpaired);
        Assert.Equal(SelectionFailure.ImageOnly, TrackSelector.Select(tracks).Failure);
    }

    [Fact]
    public async Task Ffprobe_Failure_ThrowsFfmpegException()
    {
        var runner = new RecordingRunner((_, _) => new ProcessResult(1, "", "moov atom not found"));
        var ex = await Assert.ThrowsAsync<FfmpegException>(() => new FfprobeInspector(Ff, runner).InspectAsync(@"D:\roto.mp4"));
        Assert.Contains("moov atom not found", ex.Message);
        Assert.Equal(["-v", "error", "-print_format", "json", "-show_streams", @"D:\roto.mp4"], runner.Calls.Single().Args);
    }

    // ------------------------------------------------------------------ extracción con ffmpeg

    [Fact]
    public void FfmpegArguments_MapStreamAndNeverOverwrite()
    {
        var args = FfmpegSubtitleExtractor.BuildArguments(@"D:\v\a b.mp4", 4, @"D:\v\a b.en.srt");
        Assert.Equal(["-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-i", @"D:\v\a b.mp4", "-map", "0:4", "-c:s", "srt", @"D:\v\a b.en.srt"], args);
        Assert.Equal("ass", FfmpegSubtitleExtractor.EncoderFor("x.en.ass"));
        Assert.Equal("webvtt", FfmpegSubtitleExtractor.EncoderFor("x.en.vtt"));
        Assert.Throws<ArgumentException>(() => FfmpegSubtitleExtractor.EncoderFor("x.sup"));
    }

    [Fact]
    public async Task FfmpegExtract_Success_Skip_AndFailureDeletesPartial()
    {
        var output = Path.Combine(_dir, "v.en.srt");
        var ok = new RecordingRunner((_, a) => { File.WriteAllText(a[^1], "1\n00:00:01,000 --> 00:00:02,000\nHi\n"); return new ProcessResult(0, "", ""); });
        var r = await new FfmpegSubtitleExtractor(Ff, ok).ExtractAsync(Path.Combine(_dir, "v.mp4"), 4, output);
        Assert.Equal(ExtractionStatus.Extracted, r.Status);
        Assert.Equal(Ff.FFmpeg, ok.Calls.Single().File);

        var again = await new FfmpegSubtitleExtractor(Ff, ok).ExtractAsync(Path.Combine(_dir, "v.mp4"), 4, output);
        Assert.Equal(ExtractionStatus.Skipped, again.Status);
        Assert.Single(ok.Calls);

        var output2 = Path.Combine(_dir, "w.en.srt");
        var bad = new RecordingRunner((_, a) => { File.WriteAllText(a[^1], "parcial"); return new ProcessResult(1, "", "línea 1\nStream map '0:9' matches no streams."); });
        var f = await new FfmpegSubtitleExtractor(Ff, bad).ExtractAsync(Path.Combine(_dir, "w.mp4"), 9, output2);
        Assert.Equal(ExtractionStatus.Failed, f.Status);
        Assert.Contains("matches no streams", f.Message);
        Assert.False(File.Exists(output2));
    }

    [Fact]
    public async Task Extract_FileCreatedByOtherProcessMeanwhile_IsKeptUntouched()
    {
        var output = Path.Combine(_dir, "x.en.srt");
        // Mientras "ffmpeg" trabaja, otro proceso crea la salida definitiva.
        var runner = new RecordingRunner((_, a) =>
        {
            File.WriteAllText(output, "DEL OTRO PROCESO");
            File.WriteAllText(a[^1], "1\n00:00:01,000 --> 00:00:02,000\nmío\n");
            return new ProcessResult(0, "", "");
        });
        var r = await new FfmpegSubtitleExtractor(Ff, runner).ExtractAsync(Path.Combine(_dir, "x.mp4"), 4, output);

        Assert.Equal(ExtractionStatus.Skipped, r.Status);
        Assert.Equal("DEL OTRO PROCESO", File.ReadAllText(output));
        Assert.Equal(["x.en.srt"], Directory.GetFiles(_dir).Select(Path.GetFileName)); // sin temporales

        // Y si falla con la salida ya creada por otro, tampoco se borra.
        var output3 = Path.Combine(_dir, "y.en.srt");
        var failingRace = new RecordingRunner((_, _) => { File.WriteAllText(output3, "AJENO"); return new ProcessResult(1, "", "File exists. Exiting."); });
        var f = await new FfmpegSubtitleExtractor(Ff, failingRace).ExtractAsync(Path.Combine(_dir, "y.mp4"), 4, output3);
        Assert.Equal(ExtractionStatus.Failed, f.Status);
        Assert.Equal("AJENO", File.ReadAllText(output3));
    }

    [Fact]
    public async Task Extract_Cancelled_LeavesNoFinalOrTemporaryFile()
    {
        var output = Path.Combine(_dir, "c.en.srt");
        var runner = new RecordingRunner((_, a) =>
        {
            File.WriteAllText(a[^1]["3:".Length..], "1\n00:00:01,000 --> 00:00:02,000\nmedio"); // a medias
            throw new OperationCanceledException();
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new SubtitleExtractor(Mkv, runner).ExtractAsync(Path.Combine(_dir, "c.mkv"), 3, output));

        Assert.Empty(Directory.GetFiles(_dir));
        // mkvextract recibe la ruta temporal, no la definitiva.
        Assert.StartsWith("3:", runner.Calls.Single().Args[2]);
        Assert.DoesNotContain(output, runner.Calls.Single().Args[2]);
    }

    // ------------------------------------------------------------------ enrutado y biblioteca

    [Fact]
    public async Task Toolkit_RoutesByExtension_AndLocatesOnlyTheNeededTool()
    {
        var runner = new RecordingRunner((f, _) => f == Ff.FFprobe
            ? new ProcessResult(0, Fixture("mp4_spa_sdh_eng.json"), "")
            : new ProcessResult(0, Fixture("odyssey_normal_sdh_pgs.json"), ""));
        var mkvLocated = 0;
        var toolkit = new MediaToolkit(new MediaToolDirectories(@"E:\mkv", @"E:\ff"), runner,
            d => { mkvLocated++; Assert.Equal(@"E:\mkv", d); return Mkv; },
            d => { Assert.Equal(@"E:\ff", d); return Ff; });

        var mp4 = await toolkit.InspectAsync(@"D:\a.MP4");
        Assert.Equal(0, mkvLocated);
        Assert.Equal(Ff.FFprobe, runner.Calls[^1].File);
        Assert.Equal(4, TrackSelector.Select(mp4.Tracks).Track!.Id);

        await toolkit.InspectAsync(@"D:\a.mkv");
        Assert.Equal(1, mkvLocated);
        Assert.Equal(Mkv.MkvMerge, runner.Calls[^1].File);
    }

    [Fact]
    public async Task Scanner_MissingTool_IsPerVideoError_ForMp4AndMkv()
    {
        var toolkit = new MediaToolkit(runner: new RecordingRunner((_, _) => throw new InvalidOperationException("no debe ejecutarse")),
            locateMkv: _ => throw new MkvToolNixNotFoundException("sin mkv"),
            locateFfmpeg: _ => throw new FfmpegNotFoundException("sin ffmpeg"));
        var scanner = new LibraryScanner(toolkit);

        var st = await scanner.ScanVideoAsync(Path.Combine(_dir, "a.mp4"));
        Assert.Equal("sin ffmpeg", st.Error);
        Assert.Equal(VideoPlanKind.InspectError, VideoWorkflow.Plan(st, force: false).Kind);

        Assert.False(st.MkvToolNixMissing);

        // Sin MKVToolNix solo falla ese .mkv (antes lanzaba y detenía toda la cola, aunque hubiera .mp4).
        var mkv = await scanner.ScanVideoAsync(Path.Combine(_dir, "b.mkv"));
        Assert.Equal("sin mkv", mkv.Error);
        Assert.True(mkv.MkvToolNixMissing);
        Assert.Equal(VideoPlanKind.InspectError, VideoWorkflow.Plan(mkv, force: false).Kind);
    }

    [Fact]
    public void FindVideos_IncludesMkvMp4M4v_CaseInsensitive()
    {
        foreach (var name in new[] { "a.mkv", "b.MP4", "c.m4v", "d.avi", "e.srt", "f.mp4.part" })
            File.WriteAllText(Path.Combine(_dir, name), "");
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "g.mp4"), "");

        var found = LibraryScanner.FindVideos(_dir).Select(Path.GetFileName).ToList();
        Assert.Equal(["a.mkv", "b.MP4", "c.m4v", "g.mp4"], found);
        Assert.Single(LibraryScanner.FindVideos(Path.Combine(_dir, "b.MP4")));
        Assert.Empty(LibraryScanner.FindVideos(Path.Combine(_dir, "d.avi")));
    }

    // ------------------------------------------------------------------ localizador de ffmpeg

    private static string Exe(string dir, string name) => Path.Combine(dir, OperatingSystem.IsWindows() ? name + ".exe" : name);

    [Fact]
    public void FfmpegLocator_Priority_EnvThenExtraThenPathThenRegistry()
    {
        var env = new Dictionary<string, string?> { ["SUBTOOL_FFMPEG"] = @"E:\ff", ["PATH"] = @"P:\one;P:\two" };
        var both = (string d) => new[] { Exe(d, "ffmpeg"), Exe(d, "ffprobe") };

        var exists = new HashSet<string>(both(@"E:\ff\bin").Concat(both(@"X:\extra")).Concat(both(@"P:\two")).Concat(both(@"R:\reg")),
            StringComparer.OrdinalIgnoreCase);
        // La variable acepta la raíz de la descarga (con bin\ dentro).
        Assert.Equal(Exe(@"E:\ff\bin", "ffprobe"), FfmpegLocator.Locate(k => env.GetValueOrDefault(k), exists.Contains, () => [@"X:\extra"], () => @"R:\reg").FFprobe);

        env.Remove("SUBTOOL_FFMPEG");
        Assert.StartsWith(@"X:\extra", FfmpegLocator.Locate(k => env.GetValueOrDefault(k), exists.Contains, () => [@"X:\extra"], () => @"R:\reg").FFmpeg);
        Assert.StartsWith(@"P:\two", FfmpegLocator.Locate(k => env.GetValueOrDefault(k), exists.Contains, () => [], () => @"R:\reg").FFmpeg);
        env["PATH"] = null;
        Assert.StartsWith(@"R:\reg", FfmpegLocator.Locate(k => env.GetValueOrDefault(k), exists.Contains, () => [], () => @"R:\reg").FFmpeg);
    }

    [Fact]
    public void FfmpegLocator_RequiresBothExecutables_AndExplainsHowToInstall()
    {
        var onlyFfmpeg = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Exe(@"X:\ff", "ffmpeg") };
        var ex = Assert.Throws<FfmpegNotFoundException>(() =>
            FfmpegLocator.Locate(_ => null, onlyFfmpeg.Contains, () => [@"X:\ff"], () => null));
        Assert.Contains("winget install Gyan.FFmpeg", ex.Message);
        Assert.Contains(".mp4", ex.Message);
    }

    // ------------------------------------------------------------------ registro

    [Fact]
    public void AppLog_AppendsDailyFile_AndDeletesOldOnes()
    {
        var logs = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logs);
        var old = Path.Combine(logs, "subtitulos-2026-01-01.log");
        var recent = Path.Combine(logs, "subtitulos-2026-11-01.log");
        var other = Path.Combine(logs, "20260101-000000_Peli.en.log"); // logs de traducción: no se tocan
        foreach (var f in new[] { old, recent, other })
            File.WriteAllText(f, "x");

        var log = new AppLog(logs, () => new DateTime(2026, 11, 8, 10, 30, 0));
        log.Write("app", "hola");
        log.WriteException("cli", "subtool scan", new InvalidOperationException("fallo raro"));

        var lines = File.ReadAllLines(log.CurrentFile);
        Assert.EndsWith("subtitulos-2026-11-08.log", log.CurrentFile);
        Assert.Equal("2026-11-08 10:30:00 [app] hola", lines[0]);
        Assert.Contains("[cli] ERROR subtool scan: System.InvalidOperationException: fallo raro", lines[1]);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void AppLog_NeverThrows_WhenFolderIsInvalid()
    {
        var file = Path.Combine(_dir, "soy-un-archivo");
        File.WriteAllText(file, "");
        new AppLog(Path.Combine(file, "logs")).Write("app", "no revienta"); // la carpeta no se puede crear
    }
}
