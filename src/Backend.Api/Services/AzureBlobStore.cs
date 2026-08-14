using System.Text;
using Azure;
using Azure.Storage.Blobs;

namespace Backend.Api.Services;

/// <summary>
/// IBlobStore backed by an Azure Blob Storage container. Points at Azurite locally via the well-known
/// development connection string (BlobStorage:Azure:ConnectionString="UseDevelopmentStorage=true"); in
/// production it points at a real storage account. The container is created lazily on first write so
/// there is no separate provisioning step to keep in sync with BlobStorage:Azure:ContainerName.
/// </summary>
public sealed class AzureBlobStore(IConfiguration configuration) : IBlobStore
{
    private readonly Lazy<BlobContainerClient> container = new(() =>
    {
        var connectionString = configuration["BlobStorage:Azure:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("BlobStorage:Azure:ConnectionString is not configured");
        var containerName = configuration["BlobStorage:Azure:ContainerName"] ?? "assessments";
        var client = new BlobContainerClient(connectionString, containerName);
        client.CreateIfNotExists();
        return client;
    });

    public async Task WriteAsync(string path, string content, CancellationToken ct)
    {
        var blob = container.Value.GetBlobClient(path);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await blob.UploadAsync(stream, overwrite: true, ct);
    }

    public async Task<string?> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            var blob = container.Value.GetBlobClient(path);
            var response = await blob.DownloadContentAsync(ct);
            return response.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, CancellationToken ct)
    {
        var entries = new List<BlobEntry>();
        await foreach (var item in container.Value.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, prefix, ct))
            entries.Add(new BlobEntry(item.Name, item.Properties.LastModified));
        return entries;
    }
}
