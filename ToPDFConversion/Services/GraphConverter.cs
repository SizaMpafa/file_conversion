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

    public GraphConverter(IConfiguration config)
    {
        var clientId = config["AzureAd:ClientId"] ?? throw new ArgumentNullException("AzureAd:ClientId");
        var tenantId = config["AzureAd:TenantId"] ?? throw new ArgumentNullException("AzureAd:TenantId");

        var credential = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            ClientId = clientId,
            TenantId = tenantId,
            RedirectUri = new Uri("http://localhost")
        });

        _graphClient = new GraphServiceClient(credential, new[] { "Files.ReadWrite.All", "User.Read" });
    }

    public async Task EnqueueConversionAsync(string inputPath, string outputPath)
        => await _queue.Writer.WriteAsync((inputPath, outputPath));

    public async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await _queue.Reader.WaitToReadAsync(cancellationToken))
            {
                var job = await _queue.Reader.ReadAsync(cancellationToken);
                await _semaphore.WaitAsync(cancellationToken);
                try
                {
                    await Policy
                        .Handle<Exception>()
                        .WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(Math.Pow(2, i)))
                        .ExecuteAsync(() => ConvertToPdfAsync(job.inputPath, job.outputPath));
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
    using var stream = new FileStream(inputPath, FileMode.Open, FileAccess.Read);

    var drive = await _graphClient.Me.Drive.GetAsync();
    if (drive == null)
        throw new InvalidOperationException("Unable to access OneDrive.");

    var uploaded = await _graphClient
        .Drives[drive.Id]
        .Root
        .ItemWithPath(Path.GetFileName(inputPath))
        .Content
        .PutAsync(stream);

    if (uploaded == null)
        throw new InvalidOperationException("Upload failed.");

    var pdfStream = await _graphClient
        .Drives[drive.Id]
        .Items[uploaded.Id]
        .Content
        .GetAsync(requestConfig =>
        {
            requestConfig.QueryParameters.Format = "pdf";
        });

    var dir = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(dir))
        Directory.CreateDirectory(dir);

    if (pdfStream == null)
    throw new InvalidOperationException("PDF conversion failed — no content returned.");

    using var outFile = new FileStream(outputPath, FileMode.Create);
    await pdfStream.CopyToAsync(outFile);

    await _graphClient.Drives[drive.Id].Items[uploaded.Id].DeleteAsync();
}

}
