using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Backend.Api.Services;

/// <summary>
/// Local/default ISpecStorage backend - real Azure Blob Storage in production, the Azurite emulator for
/// local Docker testing (same connection string shape, "UseDevelopmentStorage=true" as the default -
/// see appsettings.json's SpecStorage:Azure:ConnectionString). S3SpecStorage is the migration target
/// (SpecStorage:Provider = "s3", Program.cs), kept behind the same ISpecStorage contract so switching is
/// a config change, not a code change.
/// </summary>
public sealed class AzureBlobSpecStorage(BlobContainerClient container) : ISpecStorage
{
    private const string ProjectMarkerFileName = ".keep";

    public async Task<IReadOnlyList<string>> ListProjectsAsync(string clientId, CancellationToken ct)
    {
        var prefix = $"{clientId}/";
        var projects = new List<string>();
        // Positional args here are (traits, states, delimiter, prefix, ct) - easy to get backwards
        // (silently returns an empty listing instead of erroring, only caught by testing against a real
        // Azurite instance, never by the in-memory test fake) - named explicitly to rule that out.
        await foreach (var item in container.GetBlobsByHierarchyAsync(traits: BlobTraits.None, states: BlobStates.None, delimiter: "/", prefix: prefix, cancellationToken: ct))
        {
            if (!item.IsPrefix) continue;
            var projeto = item.Prefix[prefix.Length..].TrimEnd('/');
            if (projeto.Length > 0) projects.Add(projeto);
        }
        return projects.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    public async Task CreateProjectAsync(string clientId, string projeto, CancellationToken ct)
    {
        var blob = container.GetBlobClient(Key(clientId, projeto, ProjectMarkerFileName));
        await blob.UploadAsync(BinaryData.FromString(""), overwrite: true, ct);
    }

    public async Task<IReadOnlyList<string>> ListSpecFilesAsync(string clientId, string projeto, CancellationToken ct)
    {
        var prefix = $"{clientId}/{projeto}/";
        var files = new List<string>();
        await foreach (var item in container.GetBlobsByHierarchyAsync(traits: BlobTraits.None, states: BlobStates.None, delimiter: "/", prefix: prefix, cancellationToken: ct))
        {
            if (item.IsPrefix) continue;
            var name = item.Blob.Name[prefix.Length..];
            if (name.Length > 0 && name != ProjectMarkerFileName) files.Add(name);
        }
        return files.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    public async Task<string?> GetContentAsync(string clientId, string projeto, string fileName, CancellationToken ct)
    {
        try
        {
            var response = await container.GetBlobClient(Key(clientId, projeto, fileName)).DownloadContentAsync(ct);
            return response.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task SaveContentAsync(string clientId, string projeto, string fileName, string content, CancellationToken ct)
    {
        var blob = container.GetBlobClient(Key(clientId, projeto, fileName));
        await blob.UploadAsync(BinaryData.FromString(content), new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "text/markdown; charset=utf-8" },
        }, ct);
    }

    private static string Key(string clientId, string projeto, string fileName) => $"{clientId}/{projeto}/{fileName}";
}
