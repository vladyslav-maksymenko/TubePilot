using TubePilot.Infrastructure;

namespace TubePilot.Worker;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddJsonFile("secrets.json", optional: true, reloadOnChange: true);
        
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddHostedService<Worker>();

        var host = builder.Build();
        host.Run();
    }
}