namespace Backend.Api.Services;

/// <summary>
/// Blob storage for spec files, replacing the earlier git-repo-based listing (spec 2026-08-09 update,
/// seção 5.2). Path convention is fixed across every implementation: <c>{clientId}/{projeto}/{fileName}</c>
/// - "projeto" is a storage-only concept (no database row), created by writing a marker object so it
/// shows up in a listing even before any real spec file exists under it (blob storage has no real
/// directories - both Azure Blob and S3 simulate them via prefix + delimiter listing).
/// </summary>
public interface ISpecStorage
{
    Task<IReadOnlyList<string>> ListProjectsAsync(string clientId, CancellationToken ct);
    Task CreateProjectAsync(string clientId, string projeto, CancellationToken ct);
    Task<IReadOnlyList<string>> ListSpecFilesAsync(string clientId, string projeto, CancellationToken ct);
    Task<string?> GetContentAsync(string clientId, string projeto, string fileName, CancellationToken ct);
    Task SaveContentAsync(string clientId, string projeto, string fileName, string content, CancellationToken ct);
}

/// <summary>
/// Path-segment validation shared by every ISpecStorage implementation and by the endpoints that accept
/// "projeto"/"fileName" straight from the URL - these become blob storage keys, so "..", "/", and "\"
/// must be rejected before they ever reach a provider SDK (path traversal into another client's prefix,
/// or an unintended nested key).
/// </summary>
public static class SpecStoragePathSegment
{
    public static bool IsValid(string segment) =>
        segment.Length > 0
        && segment.Length <= 200
        && segment != "."
        && segment != ".."
        && !segment.Contains('/')
        && !segment.Contains('\\');
}
