using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Drive;
using TubePilot.Infrastructure.Drive.Options;
using TubePilot.Infrastructure.Drive.State;

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

        return services;
    }
}