using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Backend.Api.Tests;

public sealed class HealthEndpointsTests
{
    [Fact]
    public async Task ReturnsOkWithoutASessionCookie()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }
}
