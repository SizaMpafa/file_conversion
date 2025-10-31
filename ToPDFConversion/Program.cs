using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ToPDFConversion.Services;

namespace ToPDFConversion;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
               .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
               .AddUserSecrets<Program>()
               .AddEnvironmentVariables();

        builder.Services.AddSingleton<GraphConverter>();
        builder.Services.AddHostedService<Worker>();
        builder.Services.AddWindowsService(o => o.ServiceName = "ToPDFConversionService");

        var host = builder.Build();
        await host.RunAsync();
    }
}