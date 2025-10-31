using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ToPDFConversion.Services;

public class Worker : BackgroundService
{
    private readonly GraphConverter _converter;
    private readonly ILogger<Worker> _logger;

    public Worker(GraphConverter converter, ILogger<Worker> logger)
    {
        _converter = converter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory("input");
        Directory.CreateDirectory("output");

        var watcher = new FileSystemWatcher
        {
            Path = "input",
            Filter = "*.*",
            EnableRaisingEvents = true
        };

        watcher.Created += async (sender, e) =>
        {
            var ext = Path.GetExtension(e.Name)?.ToLowerInvariant();
            if (ext is ".docx" or ".xlsx" or ".pptx")
            {
                var outputPath = Path.Combine("output",
                    Path.GetFileNameWithoutExtension(e.Name) + ".pdf");

                _logger.LogInformation("Converting {File}", e.Name);
                await _converter.EnqueueConversionAsync(e.FullPath, outputPath);
            }
        };

        await _converter.ProcessQueueAsync(stoppingToken);
    }
}