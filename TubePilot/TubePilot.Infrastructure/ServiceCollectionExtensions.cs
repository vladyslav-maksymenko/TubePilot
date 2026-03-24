using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        var driveSection = configuration.GetSection(DriveOptions.SectionName);
        
        services.Configure<DriveOptions>(driveSection);

        var driveOptions = driveSection.Get<DriveOptions>() ?? new DriveOptions();
        
        services.AddSingleton<IDriveWatcher>(provider =>
        {
            var storeLogger = provider.GetRequiredService<ILogger<KnownFilesStore>>();
            var store = new KnownFilesStore(storeLogger);
            
            var watcherLogger = provider.GetRequiredService<ILogger<GoogleDriveWatcher>>();
            return new GoogleDriveWatcher(driveOptions, store, watcherLogger);
        });


        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));
        services.AddSingleton<IVideoProcessor, FfmpegVideoProcessor>();
        
        services.AddSingleton<TelegramBotService>();
        services.AddHostedService(sp => sp.GetRequiredService<TelegramBotService>());
        services.AddSingleton<ITelegramBotService>(sp => sp.GetRequiredService<TelegramBotService>());

        return services;
    }
}