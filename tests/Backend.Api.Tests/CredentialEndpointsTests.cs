using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Tests;

public sealed class FakeSecretStore : ISecretStore
{
    // ConcurrentBag, not List: concurrency tests fire several requests in parallel, and each hits this
    // fake from a different request thread - a plain List's Add is not thread-safe and silently drops
    // entries under real concurrent access, which previously showed up as flaky off-by-one failures
    // that had nothing to do with the production code under test.
    public System.Collections.Concurrent.ConcurrentBag<(string Key, string Value)> Stored { get; } = new();
    public System.Collections.Concurrent.ConcurrentBag<string> Deleted { get; } = new();
    public bool ShouldFail { get; set; }
    public bool ShouldFailDelete { get; set; }

    public Task<string> StoreAsync(string key, string value, CancellationToken ct)
    {
        if (ShouldFail) throw new InvalidOperationException("secret store unavailable");
        Stored.Add((key, value));
        return Task.FromResult($"{key}/value");
    }

    public Task DeleteAsync(string reference, CancellationToken ct)
    {
        if (ShouldFailDelete) throw new InvalidOperationException("secret store delete unavailable");
        Deleted.Add(reference);
        return Task.CompletedTask;
    }
}

public sealed class CredentialApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"cred-tests-{Guid.NewGuid():N}.db");
    public FakeSecretStore Secrets { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Username"] = "operator",
            ["Authentication:Password"] = "secret",
            ["Authentication:SigningKey"] = "DoBqIVy5zTyTGicih2WShaYg6goTsq0lvS7XlPiHWps=",
            ["Authentication:SecureCookie"] = "false",
            ["ConnectionStrings:Default"] = $"Data Source={_databasePath}",
            ["Analista:ApiServerBaseUrl"] = "https://analista.test",
            ["Analista:AllowedHost"] = "analista.test"
        }));
        builder.ConfigureTestServices(services => services.AddSingleton<ISecretStore>(Secrets));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            var path = _databasePath + suffix;
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }
}

public sealed class CredentialEndpointsTests
{
    private static async Task<HttpClient> AuthenticatedClient(CredentialApplicationFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<long> CreateWorkspace(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task CreateCredentialSucceedsAndNeverExposesTheToken()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspace(client);

        var response = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/credenciais", new { perfil = "dev", platform_username = "recolocarme-web", token = "super-secret-token", scopes = "Contents:RW" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret-token", raw);
        Assert.DoesNotContain("\"token\"", raw);

        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("dev", body.GetProperty("perfil").GetString());
        Assert.Equal("active", body.GetProperty("status").GetString());
        Assert.Equal("recolocarme-web", body.GetProperty("platformUsername").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("secretRef").GetString()));

        var stored = Assert.Single(factory.Secrets.Stored);
        Assert.Equal("super-secret-token", stored.Value);
    }

    [Fact]
    public async Task RegisteringANewCredentialForTheSamePerfilRevokesThePrevious()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspace(client);

        var first = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/credenciais", new { perfil = "dev", platform_username = "old-account", token = "token-1" });
        first.EnsureSuccessStatusCode();
        var second = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/credenciais", new { perfil = "dev", platform_username = "new-account", token = "token-2" });
        second.EnsureSuccessStatusCode();

        var list = await (await client.GetAsync($"/workspaces/{workspaceId}/credenciais")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, list.GetArrayLength());
        var statuses = new[] { list[0].GetProperty("status").GetString(), list[1].GetProperty("status").GetString() };
        Assert.Contains("active", statuses);
        Assert.Contains("revoked", statuses);
        var active = list[0].GetProperty("status").GetString() == "active" ? list[0] : list[1];
        Assert.Equal("new-account", active.GetProperty("platformUsername").GetString());
    }

    [Fact]
    public async Task DifferentPerfisForTheSameWorkspaceDoNotAffectEachOther()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspace(client);

        await client.PostAsJsonAsync($"/workspaces/{workspaceId}/credenciais", new { perfil = "dev", platform_username = "dev-account", token = "token-dev" });
        await client.PostAsJsonAsync($"/workspaces/{workspaceId}/credenciais", new { perfil = "qa", platform_username = "qa-account", token = "token-qa" });

        var list = await (await client.GetAsync($"/workspaces/{workspaceId}/credenciais")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, list.GetArrayLength());
        Assert.All(list.EnumerateArray(), item => Assert.Equal("active", item.GetProperty("status").GetString()));
    }

    [Theory]
    [InlineData(null, "user", "token")]
    [InlineData("not-a-real-perfil", "user", "token")]
    [InlineData("dev", "", "token")]
    [InlineData("dev", "user", "")]
    public async Task InvalidRequestsAreRejectedWithoutPersistingOrCallingTheSecretStore(string? perfil, string platformUsername, string token)
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspace(client);

        var response = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/credenciais", new { perfil, platform_username = platformUsername, token });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(factory.Secrets.Stored);
        var list = await (await client.GetAsync($"/workspaces/{workspaceId}/credenciais")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task MissingWorkspaceReturnsNotFoundForCreateAndList()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);

        var create = await client.PostAsJsonAsync("/workspaces/999999/credenciais", new { perfil = "dev", platform_username = "user", token = "token" });
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        Assert.Empty(factory.Secrets.Stored);

        var list = await client.GetAsync("/workspaces/999999/credenciais");
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
    }

    [Fact]
    public async Task SecretStoreFailureReturnsBadGatewayAndDoesNotPersistOrRevokeThePrevious()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspace(client);

        var first = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/credenciais", new { perfil = "dev", platform_username = "existing", token = "token-1" });
        first.EnsureSuccessStatusCode();

        factory.Secrets.ShouldFail = true;
        var second = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/credenciais", new { perfil = "dev", platform_username = "new", token = "token-2" });

        Assert.Equal(HttpStatusCode.BadGateway, second.StatusCode);

        var list = await (await client.GetAsync($"/workspaces/{workspaceId}/credenciais")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("active", list[0].GetProperty("status").GetString());
        Assert.Equal("existing", list[0].GetProperty("platformUsername").GetString());
    }

    [Fact]
    public async Task RequiresAuthenticatedSession()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/workspaces/1/credenciais", new { perfil = "dev", platform_username = "x", token = "y" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/workspaces/1/credenciais")).StatusCode);
    }

    [Fact]
    public async Task ConcurrentRegistrationsForTheSamePerfilNeverLeaveMoreThanOneActiveCredential()
    {
        // Security finding on PR #14: the read-check-then-revoke-then-insert in Create had no DB-level
        // guarantee, so two concurrent registrations for the same (workspace, perfil) could both leave
        // an "active" row. This doesn't assert a fixed pattern of response codes (racing HTTP requests
        // against a single SQLite file will not reliably land on one specific interleaving) - it
        // asserts the invariants that must hold regardless of how the race resolves: at most one active
        // row, every request either succeeded or was rejected (never silently lost), and every secret
        // this test's FakeSecretStore actually stored is either referenced by a surviving row or was
        // cleaned up via DeleteAsync when its DB write lost the race (the orphaned-secret finding from
        // the same review) - five requests, not two, to give the race more chances to actually occur.
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspace(client);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            client.PostAsJsonAsync($"/workspaces/{workspaceId}/credenciais", new { perfil = "dev", platform_username = $"racer-{i}", token = $"token-{i}" })));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, $"unexpected status {r.StatusCode}"));

        var list = await (await client.GetAsync($"/workspaces/{workspaceId}/credenciais")).Content.ReadFromJsonAsync<JsonElement>();
        var items = list.EnumerateArray().ToList();
        Assert.Equal(1, items.Count(item => item.GetProperty("status").GetString() == "active"));
        Assert.Equal(factory.Secrets.Stored.Count, factory.Secrets.Deleted.Count + items.Count);
    }
}
