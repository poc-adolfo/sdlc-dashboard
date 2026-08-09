using Backend.Api.Services;

namespace Backend.Api.Tests;

/// <summary>Fake ISpecStorage for tests - same {clientId}/{projeto}/{fileName} key convention as the real
/// Azure/S3 implementations, kept as plain dictionaries instead of hitting Azurite/a real bucket.</summary>
public sealed class InMemorySpecStorage : ISpecStorage
{
    private readonly Dictionary<string, string> _files = new();
    private readonly HashSet<string> _projects = new();

    public Task<IReadOnlyList<string>> ListProjectsAsync(string clientId, CancellationToken ct)
    {
        var prefix = $"{clientId}/";
        IReadOnlyList<string> result = _projects.Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => p[prefix.Length..]).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return Task.FromResult(result);
    }

    public Task CreateProjectAsync(string clientId, string projeto, CancellationToken ct)
    {
        _projects.Add($"{clientId}/{projeto}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListSpecFilesAsync(string clientId, string projeto, CancellationToken ct)
    {
        var prefix = $"{clientId}/{projeto}/";
        IReadOnlyList<string> result = _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k[prefix.Length..]).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return Task.FromResult(result);
    }

    public Task<string?> GetContentAsync(string clientId, string projeto, string fileName, CancellationToken ct) =>
        Task.FromResult(_files.TryGetValue($"{clientId}/{projeto}/{fileName}", out var content) ? content : null);

    public Task SaveContentAsync(string clientId, string projeto, string fileName, string content, CancellationToken ct)
    {
        _projects.Add($"{clientId}/{projeto}");
        _files[$"{clientId}/{projeto}/{fileName}"] = content;
        return Task.CompletedTask;
    }

    /// <summary>Test convenience: seed a file directly, skipping the CreateProjectAsync step most tests don't care about.</summary>
    public void Seed(string clientId, string projeto, string fileName, string content) => _files[$"{clientId}/{projeto}/{fileName}"] = content;

    public void RemoveFile(string clientId, string projeto, string fileName) => _files.Remove($"{clientId}/{projeto}/{fileName}");
}
