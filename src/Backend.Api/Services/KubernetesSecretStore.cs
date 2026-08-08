using System.Text;
using k8s;
using k8s.Models;

namespace Backend.Api.Services;

/// <summary>
/// Real implementation of <see cref="ISecretStore"/>: writes to a Kubernetes Secret in the app's own
/// namespace (seção 11 of frontend-operacional-sdlc-hermes.md - same cluster as Hermes/OpenWebUI, no
/// external secrets manager product introduced). Only wired up and exercised against a live cluster
/// once the application is actually deployed (WBS item 17); this class is unit-tested only through the
/// ISecretStore contract via a fake in Backend.Api.Tests, not against a real Kubernetes API server.
/// </summary>
public sealed class KubernetesSecretStore(IConfiguration configuration, IHostEnvironment environment, ILogger<KubernetesSecretStore> logger) : ISecretStore
{
    private const string DataKey = "value";
    private IKubernetes? _client;

    public async Task<string> StoreAsync(string key, string value, CancellationToken ct)
    {
        var @namespace = configuration["Kubernetes:Namespace"];
        if (string.IsNullOrWhiteSpace(@namespace))
            throw new InvalidOperationException("Kubernetes:Namespace is not configured");

        var secretName = SanitizeName($"sdlc-cred-{key}");
        var client = GetOrCreateClient();
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = secretName, NamespaceProperty = @namespace },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]> { [DataKey] = Encoding.UTF8.GetBytes(value) }
        };

        try
        {
            await client.CoreV1.CreateNamespacedSecretAsync(secret, @namespace, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Rotation: a credential for this (workspace, perfil) already has a Secret - overwrite its value in place.
            await client.CoreV1.ReplaceNamespacedSecretAsync(secret, secretName, @namespace, cancellationToken: ct);
        }

        return $"{secretName}/{DataKey}";
    }

    public async Task DeleteAsync(string reference, CancellationToken ct)
    {
        var secretName = reference.Split('/', 2)[0];
        var @namespace = configuration["Kubernetes:Namespace"];
        if (string.IsNullOrWhiteSpace(@namespace)) return;

        try
        {
            var client = GetOrCreateClient();
            await client.CoreV1.DeleteNamespacedSecretAsync(secretName, @namespace, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone (or never actually created) - deleting is idempotent from the caller's view.
        }
    }

    private IKubernetes GetOrCreateClient() => _client ??= new Kubernetes(BuildConfig());

    private KubernetesClientConfiguration BuildConfig()
    {
        // A service that writes tokens must not silently widen which cluster/context it talks to.
        // BuildDefaultConfig() can load whatever kubeconfig happens to be on the process's environment
        // (KUBECONFIG, ~/.kube/config), which is an unreviewed, possibly unrelated cluster. Outside
        // Development/Testing this path is refused outright. Even inside Development/Testing it now
        // requires an explicit opt-in (Kubernetes:AllowKubeconfigFallback=true) rather than happening
        // silently just because InClusterConfig() failed - a misconfigured deployment that ends up with
        // ASPNETCORE_ENVIRONMENT=Development/Testing must still fail closed by default.
        var fallbackAllowed = (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            && configuration.GetValue("Kubernetes:AllowKubeconfigFallback", false);

        if (!fallbackAllowed) return KubernetesClientConfiguration.InClusterConfig();

        try
        {
            return KubernetesClientConfiguration.InClusterConfig();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Not running in-cluster; falling back to the default kubeconfig for Kubernetes secret writes ({Environment}, Kubernetes:AllowKubeconfigFallback=true)", environment.EnvironmentName);
            return KubernetesClientConfiguration.BuildDefaultConfig();
        }
    }

    private static string SanitizeName(string raw)
    {
        // Kubernetes object names must be lowercase RFC 1123 subdomain labels.
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '-');
        return sb.ToString().Trim('-');
    }
}
