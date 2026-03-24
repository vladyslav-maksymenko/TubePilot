using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
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

        var contentTypeProvider = new FileExtensionContentTypeProvider();
        contentTypeProvider.Mappings[".mp4"] = "video/mp4";
        contentTypeProvider.Mappings[".webm"] = "video/webm";
        contentTypeProvider.Mappings[".mkv"] = "video/x-matroska";
        contentTypeProvider.Mappings[".avi"] = "video/x-msvideo";
        contentTypeProvider.Mappings[".mov"] = "video/quicktime";

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(processedDir),
            RequestPath = "/play",
            ContentTypeProvider = contentTypeProvider
        });
        
        app.Run();
    }
}