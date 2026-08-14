using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Tests;

public sealed class SpecProjectApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"spec-projects-tests-{Guid.NewGuid():N}.db");
    public RoutingFakeHandler Handler { get; } = new();
    public FakeBlobStore Blobs { get; } = new();

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
            ["Analista:AllowedHost"] = "analista.test",
            ["Specs:ApiServerBaseUrl"] = "https://analista.test",
            ["Specs:AllowedHost"] = "analista.test",
            ["GitHub:AppToken"] = "test-token",
            ["AzureDevOps:AppToken"] = "test-token",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("Platform").ConfigurePrimaryHttpMessageHandler(() => Handler);
            services.AddHttpClient("Analista").ConfigurePrimaryHttpMessageHandler(() => Handler);
            services.AddHttpClient("Specs").ConfigurePrimaryHttpMessageHandler(() => Handler);
            services.AddSingleton<Backend.Api.Services.IBlobStore>(Blobs);
        });
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

public sealed class SpecProjectEndpointsTests
{
    private static HttpResponseMessage AnalistaResponse(string dorJson) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { choices = new[] { new { message = new { content = dorJson } } } })
    };

    private static async Task<HttpClient> AuthenticatedClient(SpecProjectApplicationFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Creates a workspace and concludes its assessment (so Workspace.ClientId is set) - every spec-projects route requires that.</summary>
    private static async Task<long> CreateWorkspaceWithConcludedAssessment(HttpClient client, string clientName = "Acme")
    {
        var workspace = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform" });
        workspace.EnsureSuccessStatusCode();
        var workspaceId = (await workspace.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var assessment = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/assessments", new { client_name = clientName, content = "x" });
        assessment.EnsureSuccessStatusCode();
        var assessmentId = (await assessment.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var conclude = await client.PostAsync($"/workspaces/{workspaceId}/assessments/{assessmentId}/concluir", content: null);
        conclude.EnsureSuccessStatusCode();
        return workspaceId;
    }

    [Fact]
    public async Task ListProjectsIsEmptyForAFreshlyConcludedWorkspace()
    {
        using var factory = new SpecProjectApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);

        var response = await client.GetAsync($"/workspaces/{workspaceId}/spec-projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SpecProjectsRoutesRejectAWorkspaceWhoseAssessmentWasNeverConcluded()
    {
        using var factory = new SpecProjectApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspace = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform" });
        var workspaceId = (await workspace.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var response = await client.GetAsync($"/workspaces/{workspaceId}/spec-projects");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreatingAProjectMakesItAppearInTheListingEvenWithNoSpecsYet()
    {
        using var factory = new SpecProjectApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);

        var create = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/spec-projects", new { name = "checkout" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var projects = await client.GetFromJsonAsync<string[]>($"/workspaces/{workspaceId}/spec-projects");
        Assert.Equal(new[] { "checkout" }, projects);
    }

    [Fact]
    public async Task PathTraversalInProjectNameIsRejected()
    {
        using var factory = new SpecProjectApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);

        var create = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/spec-projects", new { name = "../escape" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
    }

    [Fact]
    public async Task PuttingAndGettingASpecRoundTripsItsRawContent()
    {
        using var factory = new SpecProjectApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        const string markdown = "# Checkout\n\n> Status: rascunho (2026-08-05).\n\nConteudo.\n";

        var put = await client.PutAsJsonAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md", new { content = markdown });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(markdown, await get.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetOnAMissingSpecReturnsNotFound()
    {
        using var factory = new SpecProjectApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);

        var response = await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/missing.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListSpecsParsesTitleAndStatusFromContentAndSkipsTheProjectMarker()
    {
        using var factory = new SpecProjectApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        const string markdown = "# Checkout flow\n\n> Status: rascunho (2026-08-05).\n\nConteudo.\n";
        await client.PutAsJsonAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md", new { content = markdown });

        var specs = await client.GetFromJsonAsync<JsonElement>($"/workspaces/{workspaceId}/spec-projects/checkout/specs");

        Assert.Equal(1, specs.GetArrayLength());
        var item = specs[0];
        Assert.Equal("foo.md", item.GetProperty("fileName").GetString());
        Assert.Equal("Checkout flow", item.GetProperty("title").GetString());
        Assert.Equal("rascunho", item.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SubirUsPublishesToGitHubFromBlobContentWithoutRequiringASpecIndexRow()
    {
        using var factory = new SpecProjectApplicationFactory();
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse("{\"dor_atendido\": true, \"pendencias\": []}"));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { number = 42 }) });

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        const string markdown = "# Checkout\n\n## User Story\n**Como** operador, **quero** publicar.\n\n## Criterios de aceite\n- [ ] ok\n\n## WBS - Plano de implementacao\n1. Endpoint\n";
        await client.PutAsJsonAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md", new { content = markdown });

        var response = await client.PostAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("42", body.GetProperty("pipeline_instance").GetProperty("externalRef").GetString());
        var specIdKind = body.GetProperty("pipeline_instance").GetProperty("specId").ValueKind;
        Assert.Equal(JsonValueKind.Null, specIdKind);

        var issueCall = Assert.Single(factory.Handler.Captured, c => c.Uri.AbsolutePath.EndsWith("/issues"));
        var issuePayload = JsonDocument.Parse(issueCall.Body!).RootElement;
        Assert.Equal("US: Checkout", issuePayload.GetProperty("title").GetString());
    }

    [Fact]
    public async Task SubirUsReturnsPendenciasWithoutPublishingWhenDorBlocks()
    {
        using var factory = new SpecProjectApplicationFactory();
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse("{\"dor_atendido\": false, \"pendencias\": [\"falta criterio X\"]}"));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => throw new InvalidOperationException("must not publish when DoR blocks"));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        await client.PutAsJsonAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md", new { content = "# Checkout\n" });

        var response = await client.PostAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("dor_atendido").GetBoolean());
        Assert.Equal("falta criterio X", body.GetProperty("pendencias")[0].GetString());
    }

    [Fact]
    public async Task ChatCallsTheSpecsSkillAndReturnsItsReply()
    {
        using var factory = new SpecProjectApplicationFactory();
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse("Aqui esta uma sugestao."));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        await client.PutAsJsonAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md", new { content = "# Checkout\n" });

        var response = await client.PostAsJsonAsync(
            $"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md/chat",
            new { messages = new[] { new { role = "user", content = "Pode revisar?" } } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Aqui esta uma sugestao.", body.GetProperty("reply").GetString());

        var call = Assert.Single(factory.Handler.Captured, c => c.Uri.Host == "analista.test");
        var payload = JsonDocument.Parse(call.Body!).RootElement;
        Assert.Equal("specs", payload.GetProperty("model").GetString());
        Assert.Equal("Pode revisar?", payload.GetProperty("messages")[0].GetProperty("content").GetString());
    }
}
