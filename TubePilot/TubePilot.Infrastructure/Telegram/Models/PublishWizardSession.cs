using TubePilot.Core.Contracts;

namespace TubePilot.Infrastructure.Telegram.Models;

internal sealed class PublishWizardSession(IReadOnlyList<PublishedResultContext> resultContexts, long chatId)
{
    public long ChatId { get; } = chatId;

    public IReadOnlyList<PublishedResultContext> ResultContexts { get; } =
        resultContexts is { Count: > 0 } ? resultContexts : throw new ArgumentException("At least one result context is required.", nameof(resultContexts));

    public PublishedResultContext ResultContext => ResultContexts[0];

    public bool IsBulkPublish => ResultContexts.Count > 1;

    public PublishWizardStep Step { get; set; } = PublishWizardStep.WaitingForChannel;

    public IReadOnlyList<string> AvailableChannels { get; set; } = [];

    public string ChannelName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string TitleTemplate { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public IReadOnlyList<string> Tags { get; set; } = [];

    public DateTimeOffset? ScheduledPublishAtUtc { get; set; }

    public YouTubeVideoVisibility Visibility { get; set; } = YouTubeVideoVisibility.Public;

    public int? PromptMessageId { get; set; }

    public int? SummaryMessageId { get; set; }

    public int? ProgressMessageId { get; set; }

    public int LastProgressPercent { get; set; } = -1;

    public int CurrentBulkIndex { get; set; }

    public CancellationTokenSource? UploadCancellation { get; set; }
}
