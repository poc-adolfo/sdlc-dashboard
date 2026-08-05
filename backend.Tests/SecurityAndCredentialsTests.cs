using System;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SDLC.Dashboard;

namespace SDLC.Dashboard.Tests;

public class SecurityAndCredentialsTests
{
    [Fact] public void ApiKey_is_required_and_compared_constant_time() { Assert.False(SecurityRules.IsValidApiKey(null, "configured")); Assert.False(SecurityRules.IsValidApiKey("wrong", "configured")); Assert.True(SecurityRules.IsValidApiKey("configured", "configured")); }
    [Fact] public void Tenant_access_rejects_cross_tenant_resources() { Assert.True(SecurityRules.HasTenantAccess("tenant-a", "tenant-a")); Assert.False(SecurityRules.HasTenantAccess("tenant-a", "tenant-b")); Assert.False(SecurityRules.HasTenantAccess(null, "tenant-a")); }
    [Fact] public async Task Rotation_revokes_previous_active_credential_and_creates_one_active()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DashboardDb>().UseSqlite(connection).Options;
        await using var db = new DashboardDb(options); await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace { TenantId = "tenant-a", Name = "w", Slug = "w" }; db.Workspaces.Add(workspace);
        db.Credentials.Add(new ProfileCredential { WorkspaceId = workspace.Id, Profile = "github", Status = CredentialStatus.Active }); await db.SaveChangesAsync();
        var credential = await new CredentialRotationService().RotateAsync(db, new FakeSecretStore(), workspace.Id, new CredentialInput("github", "user", "new-token", "repo"));
        Assert.Equal(CredentialStatus.Active, credential.Status); Assert.Single(await db.Credentials.Where(x => x.WorkspaceId == workspace.Id && x.Profile == "github" && x.Status == CredentialStatus.Revoked).ToListAsync()); Assert.Single(await db.Credentials.Where(x => x.WorkspaceId == workspace.Id && x.Profile == "github" && x.Status == CredentialStatus.Active).ToListAsync());
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task<string> StoreAsync(Guid workspace, string profile, string token) =>
            Task.FromResult($"secret/{workspace}/{profile}");
    }
}
