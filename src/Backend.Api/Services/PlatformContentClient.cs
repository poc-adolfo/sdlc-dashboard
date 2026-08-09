using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Backend.Persistence.Domain;

namespace Backend.Api.Services;

/// <summary>
/// Adds the platform authentication header and shares the "Platform" HttpClient for GitHub/Azure DevOps
/// calls - creating an Issue/work item (SpecUsEndpoints.Publish) and reading PR review state
/// (ReconciliationPollerService). Used to also read spec file/directory content for the pre-2026-08-09
/// git-based specs listing; that responsibility moved to ISpecStorage (blob storage, seção 5.2 update)
/// and was removed from here along with it.
/// </summary>
public sealed class PlatformContentClient(IHttpClientFactory clients, IConfiguration configuration)
{
    /// <summary>Escape hatch for requests building their own body (e.g. creating an Issue/Work Item), still on the shared "Platform" HttpClient.</summary>
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
}
