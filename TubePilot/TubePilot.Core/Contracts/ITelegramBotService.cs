namespace TubePilot.Core.Contracts;
using Domain;

public interface ITelegramBotService
{
    Task NotifyNewVideoAsync(DriveFile file, string localPath, CancellationToken ct = default);
}