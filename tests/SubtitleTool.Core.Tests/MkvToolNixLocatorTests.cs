using SubtitleTool.Core.Mkv;

namespace SubtitleTool.Core.Tests;

public class MkvToolNixLocatorTests
{
    [Fact]
    public void EnvironmentVariable_HasPriority()
    {
        var env = new Dictionary<string, string?> { ["SUBTOOL_MKVTOOLNIX"] = @"E:\Tools\MKV", ["PATH"] = "" };
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"E:\Tools\MKV\mkvmerge.exe", @"E:\Tools\MKV\mkvextract.exe",
            @"C:\Program Files\MKVToolNix\mkvmerge.exe", @"C:\Program Files\MKVToolNix\mkvextract.exe",
        };

        var paths = MkvToolNixLocator.Locate(k => env.GetValueOrDefault(k), exists.Contains);

        Assert.Equal(@"E:\Tools\MKV\mkvmerge.exe", paths.MkvMerge);
        Assert.Equal(@"E:\Tools\MKV\mkvextract.exe", paths.MkvExtract);
    }

    [Fact]
    public void FallsBackToPath()
    {
        var env = new Dictionary<string, string?> { ["PATH"] = @"C:\Windows;F:\mkv" };
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"F:\mkv\mkvmerge.exe", @"F:\mkv\mkvextract.exe" };

        var paths = MkvToolNixLocator.Locate(k => env.GetValueOrDefault(k), exists.Contains);

        Assert.Equal(@"F:\mkv\mkvmerge.exe", paths.MkvMerge);
    }

    [Fact]
    public void NotFound_ThrowsClearError()
    {
        var ex = Assert.Throws<MkvToolNixNotFoundException>(() => MkvToolNixLocator.Locate(_ => null, _ => false));

        Assert.Contains("SUBTOOL_MKVTOOLNIX", ex.Message);
    }
}
