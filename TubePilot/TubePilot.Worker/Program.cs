using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.IO;
using TubePilot.Infrastructure;

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

        // Створюємо папку для віддачі відео (Telegram-бот даватиме на неї посилання)
        var processedDir = Path.Combine(builder.Environment.ContentRootPath, "processed");
        Directory.CreateDirectory(processedDir);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(processedDir),
            RequestPath = "/play",
            // Дозволяємо браузеру програвати mp4
            ServeUnknownFileTypes = true,
            DefaultContentType = "video/mp4"
        });
        
        app.Run();
    }
}