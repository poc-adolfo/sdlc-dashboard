using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace Backend.Api.Services;

/// <summary>
/// IBlobStore backed by an S3-compatible bucket. BlobStorage:S3:ServiceUrl lets this point at a local
/// emulator (e.g. MinIO/LocalStack) instead of real AWS; when unset, the AWS SDK's default region/
/// credential resolution applies. Credentials come from the SDK's standard chain (environment,
/// instance profile, shared config) - never read from this application's own configuration.
/// </summary>
public sealed class S3BlobStore(IConfiguration configuration) : IBlobStore
{
    private readonly Lazy<(IAmazonS3 Client, string Bucket)> client = new(() =>
    {
        var bucket = configuration["BlobStorage:S3:BucketName"];
        if (string.IsNullOrWhiteSpace(bucket))
            throw new InvalidOperationException("BlobStorage:S3:BucketName is not configured");

        var config = new AmazonS3Config();
        var serviceUrl = configuration["BlobStorage:S3:ServiceUrl"];
        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            // Local emulators (MinIO/LocalStack) need path-style addressing - virtual-hosted-style
            // (the AWS default) resolves bucket.host.docker.internal, which doesn't exist locally.
            config.ServiceURL = serviceUrl;
            config.ForcePathStyle = true;
        }
        else
        {
            var region = configuration["BlobStorage:S3:Region"];
            if (!string.IsNullOrWhiteSpace(region)) config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        }

        return (new AmazonS3Client(config), bucket);
    });

    public async Task WriteAsync(string path, string content, CancellationToken ct)
    {
        var (s3, bucket) = client.Value;
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = path,
            ContentBody = content,
            ContentType = "text/markdown"
        }, ct);
    }

    public async Task<string?> ReadAsync(string path, CancellationToken ct)
    {
        var (s3, bucket) = client.Value;
        try
        {
            using var response = await s3.GetObjectAsync(bucket, path, ct);
            using var reader = new StreamReader(response.ResponseStream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // Pilot scale only (dozens of specs, seção 15, same assumption SpecListingEndpoints already makes) -
    // a single page (ListObjectsV2's default MaxKeys) is enough; no continuation-token loop yet.
    public async Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, CancellationToken ct)
    {
        var (s3, bucket) = client.Value;
        var response = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix }, ct);
        return response.S3Objects.Select(o => new BlobEntry(o.Key, o.LastModified)).ToList();
    }
}
