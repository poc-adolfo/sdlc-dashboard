using Amazon.S3;
using Amazon.S3.Model;

namespace Backend.Api.Services;

/// <summary>
/// Migration-target ISpecStorage backend (SpecStorage:Provider = "s3", Program.cs) - same
/// {clientId}/{projeto}/{fileName} key convention as AzureBlobSpecStorage, so switching providers is a
/// config change only. Works against any S3-compatible endpoint (real AWS S3 or a self-hosted server),
/// not just AWS - ServiceUrl/ForcePathStyle are set from config for that reason (Program.cs).
/// </summary>
public sealed class S3SpecStorage(IAmazonS3 s3, string bucket) : ISpecStorage
{
    private const string ProjectMarkerFileName = ".keep";

    public async Task<IReadOnlyList<string>> ListProjectsAsync(string clientId, CancellationToken ct)
    {
        var prefix = $"{clientId}/";
        var projects = new List<string>();
        string? continuationToken = null;
        do
        {
            var response = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                Delimiter = "/",
                ContinuationToken = continuationToken,
            }, ct);
            projects.AddRange(response.CommonPrefixes.Select(p => p[prefix.Length..].TrimEnd('/')).Where(x => x.Length > 0));
            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuationToken is not null);
        return projects.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    public Task CreateProjectAsync(string clientId, string projeto, CancellationToken ct) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key(clientId, projeto, ProjectMarkerFileName), ContentBody = "" }, ct);

    public async Task<IReadOnlyList<string>> ListSpecFilesAsync(string clientId, string projeto, CancellationToken ct)
    {
        var prefix = $"{clientId}/{projeto}/";
        var files = new List<string>();
        string? continuationToken = null;
        do
        {
            var response = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                Delimiter = "/",
                ContinuationToken = continuationToken,
            }, ct);
            files.AddRange(response.S3Objects.Select(o => o.Key[prefix.Length..]).Where(name => name.Length > 0 && name != ProjectMarkerFileName));
            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuationToken is not null);
        return files.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    public async Task<string?> GetContentAsync(string clientId, string projeto, string fileName, CancellationToken ct)
    {
        try
        {
            using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = Key(clientId, projeto, fileName) }, ct);
            using var reader = new StreamReader(response.ResponseStream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task SaveContentAsync(string clientId, string projeto, string fileName, string content, CancellationToken ct) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key(clientId, projeto, fileName), ContentBody = content, ContentType = "text/markdown; charset=utf-8" }, ct);

    private static string Key(string clientId, string projeto, string fileName) => $"{clientId}/{projeto}/{fileName}";
}
