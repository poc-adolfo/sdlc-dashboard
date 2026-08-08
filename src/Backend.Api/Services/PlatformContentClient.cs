using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Backend.Persistence.Domain;

namespace Backend.Api.Services;

/// <summary>
/// Reads file/directory content from a workspace's code or specs repository (GitHub or Azure DevOps)
/// and adds the platform authentication header. Shared by the "subir US" publish flow
/// (<c>SpecUsEndpoints</c>) and the draft-spec listing flow (<c>SpecListingEndpoints</c>) so the two
/// don't grow independent copies of the same GitHub/Azure DevOps request-building logic.
/// </summary>
public sealed class PlatformContentClient(IHttpClientFactory clients, IConfiguration configuration)
{
    public async Task<string?> FetchFileAsync(Workspace workspace, string repo, string path, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildFileUrl(workspace.Platform, repo, path));
            AddAuth(request, workspace.Platform);
            using var response = await clients.CreateClient("Platform").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            if (workspace.Platform == WorkspacePlatform.Github)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                return Encoding.UTF8.GetString(Convert.FromBase64String(body.GetProperty("content").GetString()!.Replace("\n", "")));
            }

            var adoBody = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return adoBody.TryGetProperty("content", out var content) ? content.GetString() : await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Lists the relative repo-root paths of the .md files directly inside <paramref name="directory"/> (one level, not recursive).</summary>
    public async Task<IReadOnlyList<string>?> ListMarkdownFilesAsync(Workspace workspace, string repo, string directory, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildDirectoryUrl(workspace.Platform, repo, directory));
            AddAuth(request, workspace.Platform);
            using var response = await clients.CreateClient("Platform").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            if (workspace.Platform == WorkspacePlatform.Github)
            {
                // The GitHub contents endpoint returns an object (not an array) when path points at a
                // file instead of a directory - treat that as "nothing to list" rather than throwing.
                if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
                return document.RootElement.EnumerateArray()
                    .Where(entry => entry.GetProperty("type").GetString() == "file"
                        && entry.GetProperty("name").GetString()!.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.GetProperty("path").GetString()!)
                    .ToList();
            }

            if (!document.RootElement.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array) return null;
            return items.EnumerateArray()
                .Where(entry => entry.TryGetProperty("isFolder", out var isFolder) && !isFolder.GetBoolean()
                    && entry.GetProperty("path").GetString()!.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.GetProperty("path").GetString()!.TrimStart('/'))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Escape hatch for requests that don't fit FetchFileAsync/ListMarkdownFilesAsync (e.g. creating an Issue/Work Item), still on the shared "Platform" HttpClient.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => clients.CreateClient("Platform").SendAsync(request, ct);

    /// <summary>
    /// Logins currently in "APPROVED" state on a GitHub PR - only the most recent review per reviewer
    /// counts (a later CHANGES_REQUESTED supersedes an earlier APPROVED from the same person). Used by
    /// ReconciliationPollerService to recover from a lost pull_request_review webhook. Azure DevOps
    /// returns null (not implemented).
    /// </summary>
    public async Task<IReadOnlyList<string>?> ListApprovedReviewerLoginsAsync(Workspace workspace, string prNumber, CancellationToken ct)
    {
        if (workspace.Platform != WorkspacePlatform.Github) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{workspace.PlatformRef}/pulls/{prNumber}/reviews?per_page=100");
            AddAuth(request, workspace.Platform);
            using var response = await clients.CreateClient("Platform").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            var latestStateByReviewer = new Dictionary<string, string>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var login = entry.TryGetProperty("user", out var user) && user.TryGetProperty("login", out var loginEl) ? loginEl.GetString() : null;
                var state = entry.TryGetProperty("state", out var stateEl) ? stateEl.GetString() : null;
                if (login is null || state is null) continue;
                latestStateByReviewer[login] = state; // GitHub returns reviews in submission order, so the last write per key wins.
            }
            return latestStateByReviewer.Where(kv => string.Equals(kv.Value, "APPROVED", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public void AddAuth(HttpRequestMessage request, WorkspacePlatform platform)
    {
        var token = configuration[platform == WorkspacePlatform.Github ? "GitHub:AppToken" : "AzureDevOps:AppToken"];
        if (string.IsNullOrWhiteSpace(token)) return;
        // Temporario: tokens globais; a tarefa 8 substituira por workspace.app_secret_ref/Kubernetes Secret.
        if (platform == WorkspacePlatform.Github)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        else
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + token)));
        request.Headers.UserAgent.ParseAdd("sdlc-dashboard");
    }

    private static string BuildFileUrl(WorkspacePlatform platform, string repo, string path) =>
        platform == WorkspacePlatform.Github
            ? $"https://api.github.com/repos/{repo}/contents/{Uri.EscapeDataString(path).Replace("%2F", "/")}"
            : BuildAdoItemUrl(repo, path);

    private static string BuildDirectoryUrl(WorkspacePlatform platform, string repo, string directory) =>
        platform == WorkspacePlatform.Github
            ? $"https://api.github.com/repos/{repo}/contents/{Uri.EscapeDataString(directory).Replace("%2F", "/")}"
            : BuildAdoDirectoryUrl(repo, directory);

    private static string BuildAdoItemUrl(string repo, string path)
    {
        var parts = repo.Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return "https://invalid/";
        return $"https://dev.azure.com/{parts[0]}/{parts[1]}/_apis/git/repositories/{parts[2]}/items?path=/{Uri.EscapeDataString(path).Replace("%2F", "/")}&includeContent=true&api-version=7.1";
    }

    private static string BuildAdoDirectoryUrl(string repo, string directory)
    {
        var parts = repo.Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return "https://invalid/";
        var scopePath = string.IsNullOrEmpty(directory) ? "/" : "/" + directory;
        return $"https://dev.azure.com/{parts[0]}/{parts[1]}/_apis/git/repositories/{parts[2]}/items?scopePath={Uri.EscapeDataString(scopePath)}&recursionLevel=OneLevel&api-version=7.1";
    }
}
