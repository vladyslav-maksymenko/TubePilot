using TubePilot.Infrastructure.Video;

namespace TubePilot.Infrastructure.Tests;

public sealed class FfmpegProgressParserTests
{
    [Fact]
    public void TryParsePercent_ParsesAndClampsProgress()
    {
        var progress = FfmpegProgressParser.TryParsePercent("out_time_us=75000000", 100d);

        Assert.Equal(75, progress);
    }

    [Fact]
    public void TryParsePercent_ClampsToNinetyNineBeforeCompletion()
    {
        var progress = FfmpegProgressParser.TryParsePercent("out_time_us=99000000", 100d);

        Assert.Equal(99, progress);
    }

    [Fact]
    public void TryParsePercent_IgnoresNonProgressLines()
    {
        var progress = FfmpegProgressParser.TryParsePercent("frame=42", 100d);

        Assert.Null(progress);
    }
}
