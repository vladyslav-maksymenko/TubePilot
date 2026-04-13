namespace TubePilot.Infrastructure.Telegram.Models;

internal enum PublishWizardStep
{
    Idle,
    WaitingForChannel,
    WaitingForTitle,
    WaitingForDescription,
    WaitingForTags,
    WaitingForScheduleChoice,
    WaitingForCustomDate,
    WaitingForPickDate,
    WaitingForPickTime,
    WaitingForVisibility,
    Confirm,
    Uploading
}
