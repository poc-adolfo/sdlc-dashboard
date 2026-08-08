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

    /// <summary>
    /// Deletes the secret a prior <see cref="StoreAsync"/> call returned <paramref name="reference"/>
    /// for. Callers use this to compensate when the write succeeded but the caller's own persistence
    /// failed afterward (e.g. a DB constraint violation) - without it, a token-bearing Secret is left
    /// behind with nothing in the database pointing at it. Must not throw when the secret is already
    /// gone (idempotent).
    /// </summary>
    Task DeleteAsync(string reference, CancellationToken ct);
}
