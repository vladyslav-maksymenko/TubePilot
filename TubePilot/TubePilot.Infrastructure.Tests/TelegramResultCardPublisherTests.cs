using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Telegram;
using TubePilot.Infrastructure.Telegram.Models;

namespace TubePilot.Infrastructure.Tests;

public sealed class TelegramResultCardPublisherTests
{
    [Fact]
    public async Task SendResultCardsAsync_SendsPhoto_WhenThumbnailExists()
    {
        var thumbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(thumbPath, [1, 2, 3]);

        var client = new FakeResultCardClient
        {
            SendPhotoHandler = _ => Task.FromResult(new Message())
        };

        var publisher = CreatePublisher(
            client,
            new FakeThumbnailGenerator(_ => Task.FromResult<string?>(thumbPath)),
            new FakeDelay());

        var contexts = new[] { CreateContext(publicUrl: "https://example.test/play/out.mp4") };
        var messages = await publisher.SendResultCardsAsync(chatId: 1, contexts, CancellationToken.None);

        Assert.Single(messages);
        Assert.Single(client.PhotoCalls);
        Assert.Empty(client.MessageCalls);
    }

    [Fact]
    public async Task SendResultCardsAsync_FallsBackToText_WhenSendPhotoThrows()
    {
        var thumbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(thumbPath, [1, 2, 3]);

        var client = new FakeResultCardClient
        {
            SendPhotoHandler = _ => throw new InvalidOperationException("boom"),
            SendMessageHandler = _ => Task.FromResult(new Message())
        };

        var publisher = CreatePublisher(
            client,
            new FakeThumbnailGenerator(_ => Task.FromResult<string?>(thumbPath)),
            new FakeDelay());

        var contexts = new[] { CreateContext(publicUrl: "https://example.test/play/out.mp4") };
        var messages = await publisher.SendResultCardsAsync(chatId: 1, contexts, CancellationToken.None);

        Assert.Single(messages);
        Assert.Single(client.PhotoCalls);
        Assert.Single(client.MessageCalls);
    }

    [Fact]
    public async Task SendResultCardsAsync_ThrottlesBetweenCards()
    {
        var client = new FakeResultCardClient
        {
            SendMessageHandler = _ => Task.FromResult(new Message())
        };
        var delay = new FakeDelay();
        var publisher = CreatePublisher(
            client,
            new FakeThumbnailGenerator(_ => Task.FromResult<string?>(null)),
            delay);

        var contexts = new[]
        {
            CreateContext(publicUrl: null),
            CreateContext(publicUrl: null),
            CreateContext(publicUrl: null)
        };

        var messages = await publisher.SendResultCardsAsync(chatId: 1, contexts, CancellationToken.None);

        Assert.Equal(3, messages.Count);
        Assert.Equal(3, client.MessageCalls.Count);
        Assert.Equal(2, delay.Delays.Count);
        Assert.All(delay.Delays, d => Assert.Equal(TimeSpan.FromMilliseconds(1500), d));
    }

    [Fact]
    public async Task SendResultCardsAsync_IncludesWatchButtonOnlyForTelegramSafeHttpsUrl()
    {
        var client = new FakeResultCardClient
        {
            SendMessageHandler = _ => Task.FromResult(new Message())
        };

        var publisher = CreatePublisher(
            client,
            new FakeThumbnailGenerator(_ => Task.FromResult<string?>(null)),
            new FakeDelay());

        var contexts = new[] { CreateContext(publicUrl: "https://example.test/play/out.mp4") };
        await publisher.SendResultCardsAsync(chatId: 1, contexts, CancellationToken.None);

        var markup = Assert.Single(client.MessageCalls).ReplyMarkup;
        var buttons = markup.InlineKeyboard.SelectMany(row => row).ToList();
        Assert.Contains(buttons, b => b.Text == "Смотреть" && b.Url == "https://example.test/play/out.mp4");
        Assert.Contains(buttons, b => b.Text.Contains("Опублікувати", StringComparison.Ordinal) && b.CallbackData == "res:publish");

        client.MessageCalls.Clear();

        await publisher.SendResultCardsAsync(chatId: 1, [CreateContext(publicUrl: "http://example.test/play/out.mp4")], CancellationToken.None);
        markup = Assert.Single(client.MessageCalls).ReplyMarkup;
        buttons = markup.InlineKeyboard.SelectMany(row => row).ToList();
        Assert.DoesNotContain(buttons, b => b.Text == "Смотреть");
    }

    private static TelegramResultCardPublisher CreatePublisher(
        FakeResultCardClient client,
        ITelegramResultThumbnailGenerator thumbnailGenerator,
        FakeDelay delay)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        return new TelegramResultCardPublisher(
            client,
            thumbnailGenerator,
            delay,
            loggerFactory.CreateLogger<TelegramResultCardPublisher>());
    }

    private static PublishedResultContext CreateContext(string? publicUrl)
    {
        var summary = new VideoProcessingSummary(
            Slice: null,
            Mirror: false,
            Volume: null,
            Speed: null,
            ColorCorrection: null,
            QrOverlay: false,
            Rotate: null,
            Downscale: null);

        return new PublishedResultContext(
            SourceFileName: "source.mp4",
            ResultFileName: "out.mp4",
            ResultFilePath: @"C:\out\out.mp4",
            PublicUrl: publicUrl,
            PartNumber: 1,
            TotalParts: 1,
            DurationSeconds: 1,
            SizeBytes: 1,
            ProcessingSummary: summary);
    }

    private sealed record PhotoCall(long ChatId, InputFile Photo, string Caption, InlineKeyboardMarkup ReplyMarkup);

    private sealed record MessageCall(long ChatId, string Text, InlineKeyboardMarkup ReplyMarkup);

    private sealed class FakeResultCardClient : ITelegramResultCardClient
    {
        public Func<PhotoCall, Task<Message>>? SendPhotoHandler { get; init; }
        public Func<MessageCall, Task<Message>>? SendMessageHandler { get; init; }

        public List<PhotoCall> PhotoCalls { get; } = [];
        public List<MessageCall> MessageCalls { get; } = [];

        public Task<Message> SendPhotoAsync(long chatId, InputFile photo, string caption, InlineKeyboardMarkup replyMarkup, CancellationToken ct)
        {
            var call = new PhotoCall(chatId, photo, caption, replyMarkup);
            PhotoCalls.Add(call);
            return SendPhotoHandler?.Invoke(call) ?? Task.FromResult(new Message());
        }

        public Task<Message> SendMessageAsync(long chatId, string text, InlineKeyboardMarkup replyMarkup, CancellationToken ct)
        {
            var call = new MessageCall(chatId, text, replyMarkup);
            MessageCalls.Add(call);
            return SendMessageHandler?.Invoke(call) ?? Task.FromResult(new Message());
        }
    }

    private sealed class FakeThumbnailGenerator(Func<string, Task<string?>> impl) : ITelegramResultThumbnailGenerator
    {
        public Task<string?> TryGenerateAsync(string videoPath, CancellationToken ct) => impl(videoPath);
    }

    private sealed class FakeDelay : IDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan duration, CancellationToken ct)
        {
            Delays.Add(duration);
            return Task.CompletedTask;
        }
    }
}
