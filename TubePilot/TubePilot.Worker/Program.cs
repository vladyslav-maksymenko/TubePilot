using TubePilot.Infrastructure;
using TubePilot.Infrastructure.Drive.Options;

namespace TubePilot.Worker;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Configuration.AddJsonFile("secrets.json", optional: true, reloadOnChange: true);
        
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddHostedService<Worker>();

        var app = builder.Build();

        var driveOptions = builder.Configuration.GetSection(DriveOptions.SectionName).Get<DriveOptions>() ?? new DriveOptions();
        var processedDir = Path.GetFullPath(driveOptions.ProcessedDirectory);
        Directory.CreateDirectory(processedDir);

        ProcessedVideoEndpoints.MapRoutes(app, processedDir);
        
        app.Run();
    }
}
