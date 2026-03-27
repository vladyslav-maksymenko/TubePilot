using TubePilot.Infrastructure.Telegram;

namespace TubePilot.Infrastructure.Tests;

public sealed class TelegramResultLinksTests
{
    [Fact]
    public void BuildPublicResultUrl_AppendsPlayRouteWithEscapedFileName()
    {
        var result = TelegramResultLinks.BuildPublicResultUrl("https://example.ngrok.app/", "video final.mp4");

        Assert.Equal("https://example.ngrok.app/play/video%20final.mp4", result);
    }

    [Fact]
    public void BuildResultMessage_WithPublicUrl_EmitsClickableAnchor()
    {
        var message = TelegramResultLinks.BuildResultMessage(
            "video.mp4",
            @"C:\processed\video.mp4",
            "https://example.ngrok.app/play/video.mp4");

        Assert.Contains("<a href=\"https://example.ngrok.app/play/video.mp4\">", message, StringComparison.Ordinal);
        Assert.Contains("📁 Локально:", message, StringComparison.Ordinal);
        Assert.DoesNotContain("🔗 URL:", message, StringComparison.Ordinal);
    }
}
