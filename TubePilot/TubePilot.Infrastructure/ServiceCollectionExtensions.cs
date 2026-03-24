using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Drive;
using TubePilot.Infrastructure.Drive.Options;
using TubePilot.Infrastructure.Drive.State;
using TubePilot.Infrastructure.Telegram;
using TubePilot.Infrastructure.Telegram.Options;
using TubePilot.Infrastructure.Video;

namespace TubePilot.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DriveOptions>(configuration.GetSection(DriveOptions.SectionName));
        services.AddSingleton<IKnownFilesStore, KnownFilesStore>();
        services.AddSingleton<IDriveWatcher, GoogleDriveWatcher>();

        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));
        services.AddSingleton<IVideoProcessor, FfmpegVideoProcessor>();
        
        services.AddSingleton<TelegramBotService>();
        services.AddHostedService(sp => sp.GetRequiredService<TelegramBotService>());
        services.AddSingleton<ITelegramBotService>(sp => sp.GetRequiredService<TelegramBotService>());

        return services;
    }
}
