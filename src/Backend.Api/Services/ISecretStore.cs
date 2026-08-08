namespace Backend.Api.Services;

/// <summary>
/// Writes a secret value to wherever it actually lives (seção 8/11 of frontend-operacional-sdlc-hermes.md:
/// a Kubernetes Secret in the same k3s cluster) and returns only the reference needed to find it again.
/// The application never persists the raw value itself - callers store the returned reference in
/// PerfilCredential.SecretRef / Workspace.AppSecretRef, never the token.
/// </summary>
public interface ISecretStore
{
    /// <summary>Writes <paramref name="value"/> under a name derived from <paramref name="key"/> and returns the "secretName/dataKey" reference to store instead of the value.</summary>
    Task<string> StoreAsync(string key, string value, CancellationToken ct);
}
