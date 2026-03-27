using TubePilot.Infrastructure.Telegram;

namespace TubePilot.Infrastructure.Tests;

public sealed class TelegramUrlSafetyTests
{
    [Theory]
    [InlineData("https://example.test/play/video.mp4", true)]
    [InlineData("http://example.test/play/video.mp4", false)]
    [InlineData("https://localhost/play/video.mp4", false)]
    [InlineData("https://127.0.0.1/play/video.mp4", false)]
    [InlineData("ftp://example.test/video.mp4", false)]
    [InlineData("not-a-url", false)]
    public void IsTelegramSafeButtonUrl_EnforcesHttpsAndDisallowsLoopback(string url, bool expected)
    {
        Assert.Equal(expected, TelegramUrlSafety.IsTelegramSafeButtonUrl(url));
    }
}

