using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

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
            ["Authentication:SigningKey"] = "DoBqIVy5zTyTGicih2WShaYg6goTsq0lvS7XlPiHWps=",
            ["Authentication:SecureCookie"] = "false",
            ["Authentication:ExpirationMinutes"] = "60",
            ["Authentication:LoginLockoutDuration"] = "00:15:00",
            ["Authentication:AccountLoginLockoutDuration"] = "00:05:00",
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

public sealed class AuthAndWorkspaceTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;

    public AuthAndWorkspaceTests(TestApplicationFactory factory)
    {
        _factory = factory;
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Backend.Api.Auth.LoginAttemptService>().Clear();
    }

    [Fact]
    public async Task WorkspaceRequiresValidSessionAndValidSessionAllowsWorkspaceAccess()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/workspaces/1")).StatusCode);

        var login = await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("sdlc_session", login.Headers.GetValues("Set-Cookie").Single());

        var create = await client.PostAsJsonAsync("/workspaces", new { Name = "Protected", Platform = "github", platform_ref = "org/repo" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    [Fact]
    public async Task MalformedLoginPayloadsAreRejectedWithoutServerError()
    {
        using var client = _factory.CreateClient();

        using var nullUsername = new StringContent("{\"username\":null,\"password\":\"secret\"}", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync("/auth/login", nullUsername)).StatusCode);

        using var missingUsername = new StringContent("{\"password\":\"secret\"}", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync("/auth/login", missingUsername)).StatusCode);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync("/auth/login", content: null)).StatusCode);

        using var malformedJson = new StringContent("{not-json", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/login", malformedJson)).StatusCode);
    }

    [Fact]
    public async Task RepeatedInvalidLoginsAreTemporarilyBlockedAndDoNotLogUsername()
    {
        using var client = _factory.CreateClient();
        for (var attempt = 1; attempt <= 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "wrong" })).StatusCode);

        var blocked = await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.Contains("300", retryAfter!);
    }


    [Fact]
    public async Task AccountLockoutAppliesAcrossDifferentClientIps()
    {
        using var first = _factory.CreateClient();
        using var second = _factory.CreateClient();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var client = attempt % 2 == 0 ? second : first;
            client.DefaultRequestHeaders.Remove("X-Forwarded-For");
            client.DefaultRequestHeaders.Add("X-Forwarded-For", $"198.51.100.{attempt}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "wrong" })).StatusCode);
        }
        var blocked = await second.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.Contains("300", retryAfter!);
    }

    [Fact]
    public void AccountLockoutUsesConfiguredFailureLimitAndExpires()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new Backend.Api.Auth.SessionOptions
        {
            Username = "operator",
            Password = "secret",
            SigningKey = "DoBqIVy5zTyTGicih2WShaYg6goTsq0lvS7XlPiHWps=",
            AccountLoginMaxFailures = 3,
            AccountLoginLockoutDuration = TimeSpan.FromMinutes(5),
            LoginAttemptEntryTtl = TimeSpan.FromMinutes(10)
        });
        var service = new Backend.Api.Auth.LoginAttemptService(options);
        var now = DateTimeOffset.UtcNow;

        service.RecordAccountFailure("operator", now);
        service.RecordAccountFailure("operator", now.AddSeconds(1));
        Assert.False(service.IsAccountBlocked("operator", now.AddSeconds(1)));

        service.RecordAccountFailure("operator", now.AddSeconds(2));
        Assert.True(service.IsAccountBlocked("operator", now.AddSeconds(2)));
        Assert.False(service.IsAccountBlocked("operator", now.AddMinutes(6)));
    }

    [Fact]
    public void LoginAttemptTrackingIsBoundedAndExpiresEntries()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new Backend.Api.Auth.SessionOptions
        { Username = "operator", Password = "secret", SigningKey = "DoBqIVy5zTyTGicih2WShaYg6goTsq0lvS7XlPiHWps=", MaxTrackedLoginIdentities = 2, LoginAttemptEntryTtl = TimeSpan.FromSeconds(1) });
        var service = new Backend.Api.Auth.LoginAttemptService(options);
        var now = DateTimeOffset.UtcNow;
        service.RecordFailure("one", now); service.RecordFailure("two", now); service.RecordFailure("three", now);
        Assert.True(service.TrackedEntryCount <= 2);
        service.RecordFailure("fresh", now.AddSeconds(2));
        Assert.True(service.TrackedEntryCount <= 1);
    }

    [Fact]
    public void SigningKeyValidationAcceptsBase64Encoded32ByteKey()
    {
        Assert.True(Backend.Api.Auth.SessionOptions.IsStrongSigningKey("DoBqIVy5zTyTGicih2WShaYg6goTsq0lvS7XlPiHWps="));
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("AQIDBA==")]
    public void SigningKeyValidationRejectsNonBase64AndShortDecodedKeys(string key)
    {
        Assert.False(Backend.Api.Auth.SessionOptions.IsStrongSigningKey(key));
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
    public async Task SwaggerRoutesRequireSessionAndAllowAuthenticatedRequests()
    {
        using var anonymous = _factory.CreateClient();
        foreach (var route in new[] { "/swagger", "/swagger/index.html", "/swagger/v1/swagger.json" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        }

        using var authenticated = await AuthenticatedClient();
        foreach (var route in new[] { "/swagger", "/swagger/index.html", "/swagger/v1/swagger.json" })
        {
            Assert.Equal(HttpStatusCode.OK, (await authenticated.GetAsync(route)).StatusCode);
        }
    }

    [Fact]
    public async Task WorkspaceCreationPersistsAndCanBeRead()
    {
        using var client = await AuthenticatedClient();
        var slug = $"workspace-{Guid.NewGuid():N}";
        var response = await client.PostAsJsonAsync("/workspaces", new { Name = "Acme", Platform = "github", platform_ref = "acme/platform" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var workspace = await response.Content.ReadFromJsonAsync<WorkspaceResponse>();
        Assert.NotNull(workspace);
        Assert.Equal("Acme", workspace!.Name);
        Assert.Equal("acme", workspace.Slug);
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
    public async Task WorkspaceMutationsRejectMissingAndInvalidSessions()
    {
        using var missing = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await missing.PostAsJsonAsync("/workspaces", new { Name = "Unauthenticated", Platform = "github", PlatformRef = "org/repo" })).StatusCode);
        using var missingPatch = new HttpRequestMessage(HttpMethod.Patch, "/workspaces/1")
        { Content = JsonContent.Create(new { Name = "Unauthenticated" }) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await missing.SendAsync(missingPatch)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await missing.PostAsync("/workspaces/1/archive", content: null)).StatusCode);

        using var invalid = _factory.CreateClient();
        invalid.DefaultRequestHeaders.Add("Cookie", "sdlc_session=invalid-or-tampered");
        Assert.Equal(HttpStatusCode.Unauthorized, (await invalid.PostAsJsonAsync("/workspaces", new { Name = "Invalid", Platform = "github", PlatformRef = "org/repo" })).StatusCode);
        using var invalidPatch = new HttpRequestMessage(HttpMethod.Patch, "/workspaces/1")
        { Content = JsonContent.Create(new { Name = "Invalid" }) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await invalid.SendAsync(invalidPatch)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await invalid.PostAsync("/workspaces/1/archive", content: null)).StatusCode);
    }

    [Fact]
    public async Task TamperedCookieAndInvalidUsernameClaimAreRejected()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "sdlc_session=operator%7C9999999999%7Ctampered");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/workspaces/1")).StatusCode);

        var service = new Backend.Api.Auth.SessionService(Microsoft.Extensions.Options.Options.Create(new Backend.Api.Auth.SessionOptions
        { Username = "operator", Password = "secret", SigningKey = "DoBqIVy5zTyTGicih2WShaYg6goTsq0lvS7XlPiHWps=" }));
        var validSignatureForEmptyUser = Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes("test-signing-key-at-least-32-characters"), System.Text.Encoding.UTF8.GetBytes("|9999999999")));
        Assert.False(service.Validate($"|9999999999|{validSignatureForEmptyUser}", DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void SessionExpiresAtBoundary()
    {
        var service = new Backend.Api.Auth.SessionService(Microsoft.Extensions.Options.Options.Create(new Backend.Api.Auth.SessionOptions
        { Username = "operator", Password = "secret", SigningKey = "DoBqIVy5zTyTGicih2WShaYg6goTsq0lvS7XlPiHWps=", ExpirationMinutes = 1 }));
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
