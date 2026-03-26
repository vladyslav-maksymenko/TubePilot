namespace TubePilot.Infrastructure.Telegram.Models;

internal enum PublishWizardStep
{
    Idle,
    WaitingForTitle,
    WaitingForDescription,
    WaitingForTags,
    WaitingForScheduleChoice,
    WaitingForCustomDate,
    Confirm,
    Uploading
}
