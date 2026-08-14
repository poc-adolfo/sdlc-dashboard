namespace Backend.Api.Services;

/// <summary>
/// Writes a text blob (currently just the assessment markdown export) to wherever it actually lives -
/// an Azure Blob container (Azurite locally) or an S3 bucket, selected by BlobStorage:Provider. Unlike
/// ISecretStore this is not the system of record: the database row is, and a write failure here must
/// never block the caller's own persistence - see AssessmentEndpoints.Upsert, which treats this as
/// best-effort and only logs on failure.
/// </summary>
public interface IBlobStore
{
    Task WriteAsync(string path, string content, CancellationToken ct);

    /// <summary>Reads the blob at <paramref name="path"/> back as text, or null if nothing is stored there.</summary>
    Task<string?> ReadAsync(string path, CancellationToken ct);

    /// <summary>Lists every blob whose name starts with <paramref name="prefix"/>, used to browse specs stored under a workspace's client (seção 5.2) - "projects" and "specs" are just prefixes here, not rows in a table.</summary>
    Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, CancellationToken ct);
}

public sealed record BlobEntry(string Name, DateTimeOffset? LastModified);
