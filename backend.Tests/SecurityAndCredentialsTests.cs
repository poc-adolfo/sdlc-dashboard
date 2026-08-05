using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SDLC.Dashboard;
using Xunit;

namespace SDLC.Dashboard.Tests;

public class SecurityAndCredentialsTests
{
    [Fact] public void ApiKey_is_required_and_compared_constant_time() { Assert.False(SecurityRules.IsValidApiKey(null, "configured")); Assert.False(SecurityRules.IsValidApiKey("wrong", "configured")); Assert.True(SecurityRules.IsValidApiKey("configured", "configured")); }
    [Fact] public void Tenant_access_rejects_cross_tenant_resources() { Assert.True(SecurityRules.HasTenantAccess("tenant-a", "tenant-a")); Assert.False(SecurityRules.HasTenantAccess("tenant-a", "tenant-b")); Assert.False(SecurityRules.HasTenantAccess(null, "tenant-a")); }

    [Fact]
    public async Task Credentials_http_requires_api_key_and_tenant_and_rejects_cross_tenant()
    {
        using var client = new WebhookPhaseFactory().CreateClient();
        var input = new CredentialInput("github", "user", "never-return-this-token", "repo");
        var noKey = await client.PostAsJsonAsync($"/api/workspaces/{Guid.NewGuid()}/credentials", input);
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        client.DefaultRequestHeaders.Add("X-API-Key", "bad");
        var badKey = await client.PostAsJsonAsync($"/api/workspaces/{Guid.NewGuid()}/credentials", input);
        Assert.Equal(HttpStatusCode.Unauthorized, badKey.StatusCode);
        client.DefaultRequestHeaders.Remove("X-API-Key");
        client.DefaultRequestHeaders.Add("X-API-Key", "integration-key");
        var noTenant = await client.PostAsJsonAsync($"/api/workspaces/{Guid.NewGuid()}/credentials", input);
        Assert.Equal(HttpStatusCode.BadRequest, noTenant.StatusCode);

        client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant-a");
        var createdWorkspace = await client.PostAsJsonAsync("/api/workspaces", new WorkspaceInput("credentials", Guid.NewGuid().ToString("N"), "github", "org/repo", "specs/", null));
        Assert.Equal(HttpStatusCode.Created, createdWorkspace.StatusCode);
        var workspace = await createdWorkspace.Content.ReadFromJsonAsync<Workspace>();
        client.DefaultRequestHeaders.Remove("X-Tenant-Id"); client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant-b");
        var forbidden = await client.GetAsync($"/api/workspaces/{workspace!.Id}/credentials");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Credential_http_response_never_contains_token()
    {
        using var client = new WebhookPhaseFactory().CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "integration-key"); client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant-token");
        var workspaceResponse = await client.PostAsJsonAsync("/api/workspaces", new WorkspaceInput("token-test", Guid.NewGuid().ToString("N"), "github", "org/token", "specs/", null));
        var workspace = await workspaceResponse.Content.ReadFromJsonAsync<Workspace>();
        var response = await client.PostAsJsonAsync($"/api/workspaces/{workspace!.Id}/credentials", new CredentialInput("github", "user", "super-secret-token", "repo"));
        var json = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.DoesNotContain("super-secret-token", json); Assert.DoesNotContain("Token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rotation_does_not_revoke_when_secret_store_fails_and_revokes_all_active_credentials()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DashboardDb>().UseSqlite(connection).Options;
        await using var db = new DashboardDb(options); await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace { TenantId = "tenant-a", Name = "w", Slug = "w" }; db.Workspaces.Add(workspace);
        db.Credentials.AddRange(new ProfileCredential { WorkspaceId = workspace.Id, Profile = "github", Status = CredentialStatus.Active }, new ProfileCredential { WorkspaceId = workspace.Id, Profile = "github", Status = CredentialStatus.Active }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CredentialRotationService().RotateAsync(db, new ThrowingSecretStore(), workspace.Id, new CredentialInput("github", "user", "", "repo")));
        Assert.Equal(2, await db.Credentials.CountAsync(x => x.Status == CredentialStatus.Active));
        await new CredentialRotationService().RotateAsync(db, new FakeSecretStore(), workspace.Id, new CredentialInput("github", "user", "new-token", "repo"));
        Assert.Equal(2, await db.Credentials.CountAsync(x => x.Status == CredentialStatus.Revoked)); Assert.Single(await db.Credentials.Where(x => x.Status == CredentialStatus.Active).ToListAsync());
    }

    private sealed class FakeSecretStore : ISecretStore { public Task<string> StoreAsync(Guid workspace, string profile, string token) => Task.FromResult($"secret/{workspace}/{profile}"); }
    private sealed class ThrowingSecretStore : ISecretStore { public Task<string> StoreAsync(Guid workspace, string profile, string token) => throw new InvalidOperationException("secret store unavailable"); }
}
