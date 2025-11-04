using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using Polly;

namespace ToPDFConversion.Services;

public class GraphConverter
{
    private readonly GraphServiceClient _graphClient;
    private readonly System.Threading.Channels.Channel<(string inputPath, string outputPath)> _queue
        = System.Threading.Channels.Channel.CreateUnbounded<(string, string)>();
    private readonly SemaphoreSlim _semaphore = new(5, 5);
    private static readonly string[] GraphScopes = new[] { "https://graph.microsoft.com/.default" };

    public GraphConverter(IConfiguration config)
    {
        var tenantId = config["AzureAd:TenantId"] ?? throw new KeyNotFoundException("AzureAd:TenantId not found in configuration.");
        var clientId = config["AzureAd:ClientId"] ?? throw new KeyNotFoundException("AzureAd:ClientId not found in configuration.");
        var clientSecret = config["AzureAd:ClientSecret"] ?? throw new KeyNotFoundException("AzureAd:ClientSecret not found in configuration.");

        var credential = new ClientSecretCredential(tenantId,clientId, clientSecret);

        _graphClient = new GraphServiceClient(credential, GraphScopes);
    }

    public async Task EnqueueConversionAsync(string inputPath, string outputPath)
        => await _queue.Writer.WriteAsync((inputPath, outputPath));

    public async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await _queue.Reader.WaitToReadAsync(cancellationToken))
            {
                var (inputPath, outputPath) = await _queue.Reader.ReadAsync(cancellationToken);
                await _semaphore.WaitAsync(cancellationToken);
                try
                {
                    await Policy
                        .Handle<Exception>()
                        .WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(Math.Pow(2, i)))
                        .ExecuteAsync(() => ConvertToPdfAsync(inputPath, outputPath));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] {ex.Message}");
                }
                finally
                {
                    _semaphore.Release();
                }
            }
        }
    }

  private async Task ConvertToPdfAsync(string inputPath, string outputPath)
    {
    Console.WriteLine($"[DEBUG] Starting conversion for: {Path.GetFileName(inputPath)}");
        using var stream = new FileStream(inputPath, FileMode.Open, FileAccess.Read);

    // var drive = await _graphClient.Me.Drive.GetAsync();
    var userId = "sizampafa972gmail.onmicrosoft.com"; 
    var drive = await _graphClient.Users[userId].Drive.GetAsync();

    _ = drive ?? throw new InvalidOperationException("Unable to access OneDrive.");


    var uploaded = await _graphClient
        .Drives[drive.Id]
        .Root
        .ItemWithPath($"Converted/{Path.GetFileName(inputPath)}")
        .Content
        .PutAsync(stream);

    _ = uploaded ?? throw new InvalidOperationException("Upload failed.");

    var pdfStream = await _graphClient
        .Drives[drive.Id]
        .Items[uploaded.Id]
        .Content
        .GetAsync(requestConfig =>
        {
            requestConfig.QueryParameters.Format = "pdf";
        });

    var dir = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrWhiteSpace(dir))
        Directory.CreateDirectory(dir);

    if (pdfStream == null)
    throw new InvalidOperationException("PDF conversion failed — no content returned.");

    using var outFile = new FileStream(outputPath, FileMode.Create);
    await pdfStream.CopyToAsync(outFile);

    await _graphClient.Drives[drive.Id].Items[uploaded.Id].DeleteAsync();
}

}
