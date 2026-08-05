using System.Security.Cryptography;
using System.Text;

namespace SDLC.Dashboard;

public sealed class ApiSecurityOptions
{
    public string ApiKey { get; set; } = "";
    public string[] AllowedOrigins { get; set; } = [];
}

public static class SecurityRules
{
    public static bool IsValidApiKey(string? supplied, string configured)
    {
        if (string.IsNullOrWhiteSpace(supplied) || string.IsNullOrWhiteSpace(configured)) return false;
        var left = Encoding.UTF8.GetBytes(supplied);
        var right = Encoding.UTF8.GetBytes(configured);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    public static bool HasTenantAccess(string? requestTenant, string? resourceTenant) =>
        !string.IsNullOrWhiteSpace(requestTenant) && !string.IsNullOrWhiteSpace(resourceTenant) &&
        string.Equals(requestTenant, resourceTenant, StringComparison.Ordinal);
}

public static class TenantContext
{
    public const string Header = "X-Tenant-Id";
    public static string? Get(HttpContext context) => context.Request.Headers[Header].FirstOrDefault();
}

public sealed class CredentialRotationService
{
    public async Task<ProfileCredential> RotateAsync(DashboardDb db, ISecretStore secrets, Guid workspaceId, CredentialInput input)
    {
        // Store first: a secret-store failure must not destroy the currently usable credentials.
        var reference = await secrets.StoreAsync(workspaceId, input.Profile, input.Token);
        foreach (var old in db.Credentials.Where(x => x.WorkspaceId == workspaceId && x.Profile == input.Profile && x.Status == CredentialStatus.Active))
        {
            old.Status = CredentialStatus.Revoked;
            old.RotatedAt = DateTimeOffset.UtcNow;
        }
        var credential = new ProfileCredential { WorkspaceId = workspaceId, Profile = input.Profile, PlatformUsername = input.PlatformUsername, SecretRef = reference, Scopes = input.Scopes, Status = CredentialStatus.Active };
        db.Credentials.Add(credential);
        await db.SaveChangesAsync();
        return credential;
    }
}
