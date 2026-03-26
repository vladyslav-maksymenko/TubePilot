namespace TubePilot.Infrastructure.Telegram.Models;

internal sealed class PublishWizardSession(PublishedResultContext resultContext, long chatId)
{
    public long ChatId { get; } = chatId;
    public PublishedResultContext ResultContext { get; } = resultContext;
    public PublishWizardStep Step { get; set; } = PublishWizardStep.WaitingForTitle;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IReadOnlyList<string> Tags { get; set; } = [];
    public DateTimeOffset? ScheduledPublishAtUtc { get; set; }
    public int? PromptMessageId { get; set; }
    public int? SummaryMessageId { get; set; }
    public int? ProgressMessageId { get; set; }
    public int LastProgressPercent { get; set; } = -1;
    public CancellationTokenSource? UploadCancellation { get; set; }
}
