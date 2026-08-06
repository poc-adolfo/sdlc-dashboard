using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Backend.Api.Tests;

public sealed class TestApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"auth-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Username"] = "operator",
            ["Authentication:Password"] = "secret",
            ["Authentication:SigningKey"] = "test-signing-key-at-least-32-characters",
            ["Authentication:SecureCookie"] = "false",
            ["Authentication:ExpirationMinutes"] = "60",
            ["ConnectionStrings:Default"] = $"Data Source={_databasePath}"
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public sealed class AuthAndWorkspaceTests(TestApplicationFactory factory) : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory = factory;

    [Fact]
    public async Task WorkspaceRequiresValidSessionAndValidSessionAllowsWorkspaceAccess()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/workspaces/1")).StatusCode);

        var login = await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("sdlc_session", login.Headers.GetValues("Set-Cookie").Single());

        var create = await client.PostAsJsonAsync("/workspaces", new { Name = "Protected", Slug = $"protected-{Guid.NewGuid():N}", PlatformRef = "org/repo" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    [Fact]
    public async Task InvalidLoginIsRejectedAndValidLoginAllowsProtectedWorkspaceAccess()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/workspaces", new { Name = "", Slug = "", PlatformRef = "" })).StatusCode);
    }

    [Fact]
    public async Task WorkspaceCreationPersistsAndCanBeRead()
    {
        using var client = await AuthenticatedClient();
        var slug = $"workspace-{Guid.NewGuid():N}";
        var response = await client.PostAsJsonAsync("/workspaces", new { Name = "Acme", Slug = slug, PlatformRef = "acme/platform" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var workspace = await response.Content.ReadFromJsonAsync<WorkspaceResponse>();
        Assert.NotNull(workspace);
        Assert.Equal("Acme", workspace!.Name);
        Assert.Equal(slug, workspace.Slug);
        Assert.Equal(response.Headers.Location?.ToString(), $"/workspaces/{workspace.Id}");

        var get = await client.GetFromJsonAsync<WorkspaceResponse>($"/workspaces/{workspace.Id}");
        Assert.NotNull(get);
        Assert.Equal(workspace.Id, get!.Id);
        Assert.Equal("acme/platform", get.PlatformRef);
    }

    [Fact]
    public async Task MissingWorkspaceReturnsNotFound()
    {
        using var client = await AuthenticatedClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/workspaces/9223372036854770000")).StatusCode);
    }

    [Fact]
    public async Task MissingCookieAndMalformedCookieAreRejected()
    {
        using var missing = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await missing.GetAsync("/workspaces/1")).StatusCode);
        using var malformed = _factory.CreateClient();
        malformed.DefaultRequestHeaders.Add("Cookie", "sdlc_session=not-a-session");
        Assert.Equal(HttpStatusCode.Unauthorized, (await malformed.GetAsync("/workspaces/1")).StatusCode);
    }

    [Fact]
    public async Task TamperedCookieAndInvalidUsernameClaimAreRejected()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "sdlc_session=operator%7C9999999999%7Ctampered");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/workspaces/1")).StatusCode);

        var service = new Backend.Api.Auth.SessionService(Microsoft.Extensions.Options.Options.Create(new Backend.Api.Auth.SessionOptions
        { Username = "operator", Password = "secret", SigningKey = "test-signing-key-at-least-32-characters" }));
        var validSignatureForEmptyUser = Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes("test-signing-key-at-least-32-characters"), System.Text.Encoding.UTF8.GetBytes("|9999999999")));
        Assert.False(service.Validate($"|9999999999|{validSignatureForEmptyUser}", DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void SessionExpiresAtBoundary()
    {
        var service = new Backend.Api.Auth.SessionService(Microsoft.Extensions.Options.Options.Create(new Backend.Api.Auth.SessionOptions
        { Username = "operator", Password = "secret", SigningKey = "test-signing-key-at-least-32-characters", ExpirationMinutes = 1 }));
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var cookie = service.Create("operator", now.AddMinutes(-1));
        Assert.False(service.Validate(cookie, now, out _));
    }

    private async Task<HttpClient> AuthenticatedClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private sealed record WorkspaceResponse(long Id, string Name, string Slug, string PlatformRef);
}