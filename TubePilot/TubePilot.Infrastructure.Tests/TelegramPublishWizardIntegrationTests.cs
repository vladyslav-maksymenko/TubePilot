using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Telegram;
using TubePilot.Infrastructure.Telegram.Models;
using TubePilot.Infrastructure.Telegram.Options;
using TubePilot.Infrastructure.YouTube;
using TubePilot.Infrastructure.YouTube.Options;

namespace TubePilot.Infrastructure.Tests;

public sealed class TelegramPublishWizardIntegrationTests
{
    [Fact]
    public async Task Wizard_SinglePublish_ScheduleNow_InvokesUploader_AndReportsUrl()
    {
        var chatId = 10L;
        var nowUtc = DateTimeOffset.Parse("2026-01-15T07:00:00+00:00");

        var botClient = TelegramBotClientStub.Create(out _);
        var ui = new FakeUiClient();
        var uploader = new FakeYouTubeUploader(req => new YouTubeUploadResult("vid_1", "https://youtube.test/watch?v=vid_1", YouTubeUploadStatus.Published, null));
        var sheets = new FakeSheetsLogger();
        var service = CreateService(chatId, botClient, ui, uploader, sheets, new FrozenTimeProvider(nowUtc), dailyPublishTime: "10:00");

        var groupId = 123;
        service.DebugRegisterPublishedResultGroup(chatId, groupId, [CreateResult(partNumber: 1, totalParts: 1)]);

        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: $"res:publish:{groupId}:0"), CancellationToken.None);
        Assert.Contains(
            ui.SendMessageCalls,
            c => c.ReplyMarkup?.InlineKeyboard.SelectMany(row => row).Any(b => b.CallbackData == "pw:channel:0") == true);

        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:channel:0"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "My Title"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "My Description"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "tag1, tag2"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:schedule-now"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:confirm"), CancellationToken.None);
        await service.DebugWaitForActivePublishJobAsync(chatId);

        Assert.Single(uploader.Requests);
        Assert.Equal("My Title", uploader.Requests[0].Title);
        Assert.Equal("My Description", uploader.Requests[0].Description);
        Assert.Equal(["tag1", "tag2"], uploader.Requests[0].Tags);
        Assert.Null(uploader.Requests[0].ScheduledPublishAtUtc);

        Assert.Contains(ui.EditMessageTextCalls, c => c.Text.Contains("https://youtube.test/watch?v=vid_1", StringComparison.Ordinal));
        Assert.Single(sheets.Calls);
    }

    [Fact]
    public async Task Wizard_SinglePublish_ScheduleNextFreeSlot_SetsScheduledUtc()
    {
        var chatId = 10L;
        var nowUtc = DateTimeOffset.Parse("2026-01-15T07:00:00+00:00"); // 09:00 local
        var dailyPublishTime = "10:00"; // 10:00 local => 08:00Z

        var botClient = TelegramBotClientStub.Create(out _);
        var ui = new FakeUiClient();
        var uploader = new FakeYouTubeUploader(req => new YouTubeUploadResult("vid_1", "https://youtube.test/watch?v=vid_1", YouTubeUploadStatus.Scheduled, req.ScheduledPublishAtUtc));
        var sheets = new FakeSheetsLogger();
        var timeProvider = new FrozenTimeProvider(nowUtc);
        var service = CreateService(chatId, botClient, ui, uploader, sheets, timeProvider, dailyPublishTime);

        var groupId = 123;
        service.DebugRegisterPublishedResultGroup(chatId, groupId, [CreateResult(partNumber: 1, totalParts: 1)]);

        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: $"res:publish:{groupId}:0"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:channel:0"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "My Title"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "/skip"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "/skip"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:schedule-next"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:confirm"), CancellationToken.None);
        await service.DebugWaitForActivePublishJobAsync(chatId);

        Assert.Single(uploader.Requests);
        var expected = PublishingScheduleHelper.GetNextFreeSlotUtc(nowUtc, null, "Europe/Kiev", dailyPublishTime);
        Assert.Equal(expected, uploader.Requests[0].ScheduledPublishAtUtc);
    }

    [Fact]
    public async Task Wizard_SinglePublish_PickDate_InvalidOrPast_IsRejected()
    {
        var chatId = 10L;
        var nowUtc = DateTimeOffset.Parse("2026-01-15T07:00:00+00:00");

        var botClient = TelegramBotClientStub.Create(out _);
        var ui = new FakeUiClient();
        var uploader = new FakeYouTubeUploader(_ => throw new InvalidOperationException("Should not upload"));
        var sheets = new FakeSheetsLogger();
        var service = CreateService(chatId, botClient, ui, uploader, sheets, new FrozenTimeProvider(nowUtc), dailyPublishTime: "10:00");

        var groupId = 123;
        service.DebugRegisterPublishedResultGroup(chatId, groupId, [CreateResult(partNumber: 1, totalParts: 1)]);

        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: $"res:publish:{groupId}:0"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:channel:0"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "My Title"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "/skip"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "/skip"), CancellationToken.None);

        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:schedule-pick"), CancellationToken.None);

        var before = ui.SendMessageCalls.Count;
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "not-a-date"), CancellationToken.None);
        Assert.True(ui.SendMessageCalls.Count > before);
        Assert.False(string.IsNullOrWhiteSpace(ui.SendMessageCalls[^1].Text));

        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "2000-01-01 00:00"), CancellationToken.None);
        Assert.Empty(uploader.Requests);
    }

    [Fact]
    public async Task Wizard_SinglePublish_PickDate_Success_SetsScheduledUtc_AndInvokesUploader()
    {
        var chatId = 10L;
        var nowUtc = DateTimeOffset.Parse("2026-01-15T07:00:00+00:00");

        var botClient = TelegramBotClientStub.Create(out _);
        var ui = new FakeUiClient();
        var uploader = new FakeYouTubeUploader(req => new YouTubeUploadResult("vid_1", "https://youtube.test/watch?v=vid_1", YouTubeUploadStatus.Scheduled, req.ScheduledPublishAtUtc));
        var sheets = new FakeSheetsLogger();
        var service = CreateService(chatId, botClient, ui, uploader, sheets, new FrozenTimeProvider(nowUtc), dailyPublishTime: "10:00");

        var groupId = 123;
        service.DebugRegisterPublishedResultGroup(chatId, groupId, [CreateResult(partNumber: 1, totalParts: 1)]);

        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: $"res:publish:{groupId}:0"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:channel:0"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "My Title"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "/skip"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "/skip"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:schedule-pick"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "2026-01-20 10:00"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:confirm"), CancellationToken.None);
        await service.DebugWaitForActivePublishJobAsync(chatId);

        Assert.Single(uploader.Requests);
        Assert.Equal(DateTimeOffset.Parse("2026-01-20T08:00:00+00:00"), uploader.Requests[0].ScheduledPublishAtUtc);
        Assert.Contains(ui.EditMessageTextCalls, c => c.Text.Contains("https://youtube.test/watch?v=vid_1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wizard_ClickPublishWhileActive_ShowsFinishOrCancelWarning()
    {
        var chatId = 10L;
        var botClient = TelegramBotClientStub.Create(out _);
        var ui = new FakeUiClient();
        var uploader = new FakeYouTubeUploader(_ => throw new InvalidOperationException("Should not upload"));
        var sheets = new FakeSheetsLogger();
        var service = CreateService(chatId, botClient, ui, uploader, sheets, new FrozenTimeProvider(DateTimeOffset.Parse("2026-01-15T07:00:00+00:00")), dailyPublishTime: "10:00");

        var groupId = 123;
        service.DebugRegisterPublishedResultGroup(chatId, groupId, [CreateResult(partNumber: 1, totalParts: 1)]);

        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: $"res:publish:{groupId}:0"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: $"res:publish:{groupId}:0"), CancellationToken.None);

        Assert.Contains(ui.AnswerCallbackQueryCalls, c => c.Text?.Contains("wizard", StringComparison.OrdinalIgnoreCase) == true && c.ShowAlert == true);
    }

    [Fact]
    public async Task Wizard_BulkPublish_RejectsMissingTemplatePlaceholder_ThenUploadsAllSegments_WithIncrementedSchedules()
    {
        var chatId = 10L;
        var nowUtc = DateTimeOffset.Parse("2026-01-15T07:00:00+00:00");
        var timeProvider = new FrozenTimeProvider(nowUtc);

        var botClient = TelegramBotClientStub.Create(out _);
        var ui = new FakeUiClient();
        var uploader = new FakeYouTubeUploader(req =>
        {
            var n = req.Title.Contains("1", StringComparison.Ordinal) ? 1 : req.Title.Contains("2", StringComparison.Ordinal) ? 2 : 3;
            return new YouTubeUploadResult($"vid_{n}", $"https://youtube.test/watch?v=vid_{n}", YouTubeUploadStatus.Scheduled, req.ScheduledPublishAtUtc);
        });
        var sheets = new FakeSheetsLogger();
        var service = CreateService(chatId, botClient, ui, uploader, sheets, timeProvider, dailyPublishTime: "10:00");

        var groupId = 777;
        service.DebugRegisterPublishedResultGroup(chatId, groupId,
        [
            CreateResult(partNumber: 1, totalParts: 3),
            CreateResult(partNumber: 2, totalParts: 3),
            CreateResult(partNumber: 3, totalParts: 3)
        ]);

        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: $"res:publish-all:{groupId}"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:channel:0"), CancellationToken.None);

        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "My Series"), CancellationToken.None);
        Assert.Contains(ui.SendMessageCalls, c => c.Text.Contains("{N}", StringComparison.Ordinal));

        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "Series Part {N}"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "/skip"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "/skip"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:schedule-pick"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "2026-01-20 10:00"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:confirm"), CancellationToken.None);
        await service.DebugWaitForActivePublishJobAsync(chatId);

        Assert.Equal(3, uploader.Requests.Count);
        Assert.Equal("Series Part 1", uploader.Requests[0].Title);
        Assert.Equal("Series Part 2", uploader.Requests[1].Title);
        Assert.Equal("Series Part 3", uploader.Requests[2].Title);

        var baseUtc = uploader.Requests[0].ScheduledPublishAtUtc!.Value;
        var expected2 = PublishingScheduleHelper.AddLocalDays(baseUtc, 1, "Europe/Kiev");
        var expected3 = PublishingScheduleHelper.AddLocalDays(baseUtc, 2, "Europe/Kiev");
        Assert.Equal(expected2, uploader.Requests[1].ScheduledPublishAtUtc);
        Assert.Equal(expected3, uploader.Requests[2].ScheduledPublishAtUtc);

        Assert.Contains(ui.EditMessageTextCalls, c => c.Text.Contains("Bulk upload completed", StringComparison.Ordinal));
        Assert.Contains(ui.EditMessageTextCalls, c => c.Text.Contains("https://youtube.test/watch?v=vid_1", StringComparison.Ordinal));
        Assert.Contains(ui.EditMessageTextCalls, c => c.Text.Contains("https://youtube.test/watch?v=vid_2", StringComparison.Ordinal));
        Assert.Contains(ui.EditMessageTextCalls, c => c.Text.Contains("https://youtube.test/watch?v=vid_3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wizard_BulkPublish_TemplateWithoutPlaceholder_IsRejected()
    {
        var chatId = 10L;
        var botClient = TelegramBotClientStub.Create(out _);
        var ui = new FakeUiClient();
        var uploader = new FakeYouTubeUploader(_ => throw new InvalidOperationException("Should not upload"));
        var sheets = new FakeSheetsLogger();
        var service = CreateService(chatId, botClient, ui, uploader, sheets, new FrozenTimeProvider(DateTimeOffset.Parse("2026-01-15T07:00:00+00:00")), dailyPublishTime: "10:00");

        var groupId = 777;
        service.DebugRegisterPublishedResultGroup(chatId, groupId,
        [
            CreateResult(partNumber: 1, totalParts: 2),
            CreateResult(partNumber: 2, totalParts: 2)
        ]);

        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: $"res:publish-all:{groupId}"), CancellationToken.None);
        await service.HandleUpdateAsync(botClient, CreateCallbackUpdate(chatId, msgId: 500, data: "pw:channel:0"), CancellationToken.None);

        await service.HandleUpdateAsync(botClient, CreateMessageUpdate(chatId, "Hello"), CancellationToken.None);
        Assert.Contains(ui.SendMessageCalls, c => c.Text.Contains("{N}", StringComparison.Ordinal));
        Assert.Empty(uploader.Requests);
    }

    private static TelegramBotService CreateService(
        long allowedChatId,
        ITelegramBotClient botClient,
        ITelegramUiClient uiClient,
        IYouTubeUploader youTubeUploader,
        IGoogleSheetsLogger sheetsLogger,
        TimeProvider timeProvider,
        string dailyPublishTime)
    {
        var telegramOptions = new StaticOptionsMonitor<TelegramOptions>(new TelegramOptions
        {
            BotToken = "test-token",
            AllowedChatId = allowedChatId,
            BaseUrl = "http://localhost:5000"
        });

        var publishingOptions = new StaticOptionsMonitor<PublishingOptions>(new PublishingOptions
        {
            TimeZoneId = "Europe/Kiev",
            DailyPublishTime = dailyPublishTime,
            YouTubeChannels = ["TestChannel"]
        });

        var youTubeOptions = new StaticOptionsMonitor<YouTubeOptions>(new YouTubeOptions
        {
            DefaultCategoryId = "22"
        });

        var processingQueue = new TelegramProcessingQueue(1, NullLogger<TelegramProcessingQueue>.Instance);

        var resultCardPublisher = new TelegramResultCardPublisher(
            new FakeResultCardClient(),
            new FakeThumbnailGenerator(),
            new FakeDelay(),
            NullLogger<TelegramResultCardPublisher>.Instance);

        return new TelegramBotService(
            telegramOptions,
            publishingOptions,
            youTubeOptions,
            botClient,
            uiClient,
            new FakeVideoProcessor(),
            youTubeUploader,
            sheetsLogger,
            new FakeYouTubeChannelLookup(),
            processingQueue,
            resultCardPublisher,
            new FakeThumbnailGenerator(),
            timeProvider,
            NullLogger<TelegramBotService>.Instance);
    }

    private sealed class FakeYouTubeChannelLookup : IYouTubeChannelLookup
    {
        public Task<IReadOnlyList<YouTubeChannelInfo>> GetChannelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<YouTubeChannelInfo>>([]);
    }

    private static PublishedResultContext CreateResult(int partNumber, int totalParts)
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
            ResultFileName: $"out_{partNumber}.mp4",
            ResultFilePath: $"C:\\\\tmp\\\\out_{partNumber}.mp4",
            PublicUrl: null,
            PartNumber: partNumber,
            TotalParts: totalParts,
            DurationSeconds: 1,
            SizeBytes: 1,
            ProcessingSummary: summary);
    }

    private static Update CreateCallbackUpdate(long chatId, int msgId, string data)
        => new()
        {
            Id = Random.Shared.Next(1, int.MaxValue),
            CallbackQuery = new CallbackQuery
            {
                Id = Guid.NewGuid().ToString("N"),
                Data = data,
                Message = new Message
                {
                    Chat = new Chat { Id = chatId }
                }
            }
        };

    private static Update CreateMessageUpdate(long chatId, string text)
        => new()
        {
            Id = Random.Shared.Next(1, int.MaxValue),
            Message = new Message
            {
                Chat = new Chat { Id = chatId },
                Text = text
            }
        };

    private sealed class FakeUiClient : ITelegramUiClient
    {
        private int _messageIdCounter = 100;

        public List<SendMessageCall> SendMessageCalls { get; } = [];
        public List<EditMessageTextCall> EditMessageTextCalls { get; } = [];
        public List<AnswerCallbackQueryCall> AnswerCallbackQueryCalls { get; } = [];

        public Task<int> SendMessageAsync(
            long chatId,
            string text,
            ParseMode? parseMode = null,
            InlineKeyboardMarkup? replyMarkup = null,
            CancellationToken ct = default)
        {
            SendMessageCalls.Add(new SendMessageCall(chatId, text, parseMode, replyMarkup));
            return Task.FromResult(Interlocked.Increment(ref _messageIdCounter));
        }

        public Task EditMessageTextAsync(
            long chatId,
            int messageId,
            string text,
            ParseMode? parseMode = null,
            InlineKeyboardMarkup? replyMarkup = null,
            CancellationToken ct = default)
        {
            EditMessageTextCalls.Add(new EditMessageTextCall(chatId, messageId, text, parseMode, replyMarkup));
            return Task.CompletedTask;
        }

        public Task AnswerCallbackQueryAsync(
            string callbackQueryId,
            string? text = null,
            bool showAlert = false,
            CancellationToken ct = default)
        {
            AnswerCallbackQueryCalls.Add(new AnswerCallbackQueryCall(callbackQueryId, text, showAlert));
            return Task.CompletedTask;
        }

        public sealed record SendMessageCall(long ChatId, string Text, ParseMode? ParseMode, InlineKeyboardMarkup? ReplyMarkup);
        public sealed record EditMessageTextCall(long ChatId, int MessageId, string Text, ParseMode? ParseMode, InlineKeyboardMarkup? ReplyMarkup);
        public sealed record AnswerCallbackQueryCall(string Id, string? Text, bool ShowAlert);
    }

    private sealed class FakeVideoProcessor : IVideoProcessor
    {
        public Task<IReadOnlyList<VideoProcessingResult>> ProcessAsync(string inputPath, HashSet<string> options, Func<VideoProcessingProgress, Task> progressCallback, CancellationToken ct = default)
            => throw new NotSupportedException("Not used by publish-wizard tests.");
    }

    private sealed class FakeSheetsLogger : IGoogleSheetsLogger
    {
        public List<(string SourceFile, string Title, string YoutubeId, string YoutubeUrl, string Status, DateTimeOffset? ScheduledAtUtc)> Calls { get; } = [];

        public Task LogUploadAsync(string sourceFile, string title, string youtubeId, string youtubeUrl, string status, DateTimeOffset? scheduledAtUtc, CancellationToken ct = default)
        {
            Calls.Add((sourceFile, title, youtubeId, youtubeUrl, status, scheduledAtUtc));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeYouTubeUploader(Func<YouTubeUploadRequest, YouTubeUploadResult> resultFactory) : IYouTubeUploader
    {
        public List<YouTubeUploadRequest> Requests { get; } = [];

        public async Task<YouTubeUploadResult> UploadAsync(YouTubeUploadRequest request, Func<int, Task> progressCallback, CancellationToken ct = default)
        {
            Requests.Add(request);
            await progressCallback(0);
            await progressCallback(50);
            await progressCallback(100);
            return resultFactory(request);
        }
    }

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long GetTimestamp() => 0;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable OnChange(Action<T, string> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static NullDisposable Instance { get; } = new();
            public void Dispose() { }
        }
    }

    private sealed class FakeDelay : IDelay
    {
        public Task DelayAsync(TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeResultCardClient : ITelegramResultCardClient
    {
        public Task<Message> SendPhotoAsync(long chatId, InputFile photo, string caption, InlineKeyboardMarkup replyMarkup, CancellationToken ct)
            => Task.FromResult(new Message());

        public Task<Message> SendMessageAsync(long chatId, string text, InlineKeyboardMarkup replyMarkup, CancellationToken ct)
            => Task.FromResult(new Message());
    }

    private sealed class FakeThumbnailGenerator : ITelegramResultThumbnailGenerator
    {
        public Task<string?> TryGenerateAsync(string videoPath, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private class TelegramBotClientStub : DispatchProxy
    {
        public List<SendMessageCall> SendMessageCalls { get; } = [];
        public List<EditMessageTextCall> EditMessageTextCalls { get; } = [];
        public List<AnswerCallbackQueryCall> AnswerCallbackQueryCalls { get; } = [];

        public static ITelegramBotClient Create(out TelegramBotClientStub stub)
        {
            var client = DispatchProxy.Create<ITelegramBotClient, TelegramBotClientStub>();
            stub = (TelegramBotClientStub)(object)client;
            return client;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                throw new InvalidOperationException("Missing method info.");
            }

            args ??= [];

            return targetMethod.Name switch
            {
                "SendMessage" or "SendTextMessageAsync" => HandleSendMessage(targetMethod, args),
                "EditMessageText" or "EditMessageTextAsync" => HandleEditMessageText(targetMethod, args),
                "AnswerCallbackQuery" or "AnswerCallbackQueryAsync" => HandleAnswerCallbackQuery(targetMethod, args),
                _ => HandleFallback(targetMethod)
            };
        }

        private object HandleSendMessage(MethodInfo method, object?[] args)
        {
            var text = args.OfType<string>().FirstOrDefault() ?? string.Empty;
            var replyMarkup = args.OfType<InlineKeyboardMarkup>().FirstOrDefault();
            SendMessageCalls.Add(new SendMessageCall(text, replyMarkup));

            var message = new Message();
            return ToTaskResult(method.ReturnType, message);
        }

        private object HandleEditMessageText(MethodInfo method, object?[] args)
        {
            var messageId = args.OfType<int>().FirstOrDefault();
            var text = args.OfType<string>().FirstOrDefault() ?? string.Empty;
            EditMessageTextCalls.Add(new EditMessageTextCall(messageId, text));

            var message = new Message();
            return ToTaskResult(method.ReturnType, message);
        }

        private object HandleAnswerCallbackQuery(MethodInfo method, object?[] args)
        {
            var id = args.OfType<string>().FirstOrDefault() ?? string.Empty;
            var text = args.OfType<string>().Skip(1).FirstOrDefault();
            var showAlert = args.OfType<bool>().FirstOrDefault();
            if (!showAlert)
            {
                showAlert = args.OfType<bool?>().FirstOrDefault() ?? false;
            }
            AnswerCallbackQueryCalls.Add(new AnswerCallbackQueryCall(id, text, showAlert));

            return method.ReturnType == typeof(Task) ? Task.CompletedTask : ToTaskResult(method.ReturnType, null);
        }

        private static object HandleFallback(MethodInfo method)
        {
            if (method.ReturnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var t = method.ReturnType.GetGenericArguments()[0];
                var fromResult = typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(m => m.Name == "FromResult" && m.IsGenericMethodDefinition)
                    .MakeGenericMethod(t);
                return fromResult.Invoke(null, [t.IsValueType ? Activator.CreateInstance(t) : null])!;
            }

            return null;
        }

        private static object ToTaskResult(Type returnType, object? value)
        {
            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var t = returnType.GetGenericArguments()[0];
                var fromResult = typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(m => m.Name == "FromResult" && m.IsGenericMethodDefinition)
                    .MakeGenericMethod(t);
                return fromResult.Invoke(null, [value])!;
            }

            throw new NotSupportedException($"Unexpected return type: {returnType.FullName}");
        }

        public sealed record SendMessageCall(string Text, InlineKeyboardMarkup? ReplyMarkup);

        public sealed record EditMessageTextCall(int MessageId, string Text);

        public sealed record AnswerCallbackQueryCall(string Id, string? Text, bool ShowAlert);
    }
}
