using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Backend.Api.Tests;

public sealed class HealthApplicationFactory : WebApplicationFactory<Program>
{
    public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), $"health-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Username"] = "operator",
            ["Authentication:Password"] = "secret",
            ["Authentication:SigningKey"] = "DoBqIVy5zTyTGicih2WShaYg6goTsq0lvS7XlPiHWps=",
            ["Authentication:SecureCookie"] = "false",
            ["ConnectionStrings:Default"] = $"Data Source={DatabasePath}",
            ["Analista:ApiServerBaseUrl"] = "https://analista.test",
            ["Analista:AllowedHost"] = "analista.test",
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            var path = DatabasePath + suffix;
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }
}

public sealed class HealthEndpointsTests
{
    [Fact]
    public async Task ReturnsOkWithoutASessionCookie()
    {
        using var factory = new HealthApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task NonGetHealthzStillRequiresASession()
    {
        // QA finding on PR #28: only the GET is exempted from SessionMiddleware (SessionMiddleware.cs) -
        // no MapPost exists for /healthz either, so a POST must still be rejected for lack of a session,
        // not silently 404 past the auth check.
        using var factory = new HealthApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/healthz", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReturnsServiceUnavailableWhenTheDatabaseIsUnreachable()
    {
        // QA finding on PR #28: the 503 branch (CanConnectAsync returning false) had no test. Let
        // Database.Migrate() succeed against a real file first (same as every other factory here), then
        // take an exclusive OS-level lock on it so the next connection attempt genuinely fails - a fake
        // connection string would instead fail at host startup (Migrate() itself), never reaching the
        // endpoint.
        using var factory = new HealthApplicationFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/healthz"); // forces host startup / Database.Migrate() against DatabasePath

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var exclusiveLock = new FileStream(factory.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
