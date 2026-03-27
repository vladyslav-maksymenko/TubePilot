using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Drive;
using TubePilot.Infrastructure.Drive.Options;
using TubePilot.Infrastructure.Drive.State;
using TubePilot.Infrastructure.GoogleSheets;
using TubePilot.Infrastructure.GoogleSheets.Options;
using TubePilot.Infrastructure.Telegram;
using TubePilot.Infrastructure.Telegram.Options;
using TubePilot.Infrastructure.Video;
using TubePilot.Infrastructure.YouTube;
using TubePilot.Infrastructure.YouTube.Options;

namespace TubePilot.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DriveOptions>(configuration.GetSection(DriveOptions.SectionName));
        services.Configure<GoogleSheetsOptions>(configuration.GetSection(GoogleSheetsOptions.SectionName));
        services.AddSingleton<IKnownFilesStore, KnownFilesStore>();
        services.AddSingleton<IDriveWatcher, GoogleDriveWatcher>();

        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));
        services.Configure<PublishingOptions>(configuration.GetSection(PublishingOptions.SectionName));
        services.AddSingleton(sp => new TelegramProcessingQueue(
            Math.Max(1, sp.GetRequiredService<IOptionsMonitor<TelegramOptions>>().CurrentValue.MaxConcurrentJobs),
            sp.GetRequiredService<ILogger<TelegramProcessingQueue>>()));
        services.AddSingleton<TelegramResultThumbnailGenerator>();
        services.AddSingleton<IFfmpegRunner, FfmpegRunner>();
        services.AddSingleton<IVideoProcessor, FfmpegVideoProcessor>();
        services.AddSingleton<IGoogleSheetsLogger, GoogleSheetsLogger>();

        services.Configure<YouTubeOptions>(configuration.GetSection(YouTubeOptions.SectionName));
        services.AddHttpClient();
        services.AddHttpClient<OAuthRefreshTokenAccessTokenProvider>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IYouTubeAccessTokenProvider>(sp => sp.GetRequiredService<OAuthRefreshTokenAccessTokenProvider>());
        services.AddHttpClient<IYouTubeUploader, YouTubeUploader>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        
        services.AddSingleton<TelegramBotService>();
        services.AddHostedService(sp => sp.GetRequiredService<TelegramBotService>());
        services.AddSingleton<ITelegramBotService>(sp => sp.GetRequiredService<TelegramBotService>());

        return services;
    }
}
