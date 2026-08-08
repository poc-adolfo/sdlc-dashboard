using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Backend.Persistence.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Tests;

/// <summary>Routes fake HTTP responses by predicate; used to stand in for GitHub/Azure DevOps/Analista over HTTP in tests.</summary>
public sealed class RoutingFakeHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = new();

    public void On(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond) => _routes.Add((match, respond));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        foreach (var (match, respond) in _routes)
        {
            if (match(request)) return Task.FromResult(respond(request));
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"no fake route registered for {request.Method} {request.RequestUri}")
        });
    }

    // The factory keeps this handler alive across the pooled-handler lifetime that IHttpClientFactory manages internally.
    protected override void Dispose(bool disposing) { }
}

public sealed class SpecUsApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"specus-tests-{Guid.NewGuid():N}.db");
    public RoutingFakeHandler Handler { get; } = new();
    public string AnalistaBaseUrl { get; set; } = "https://analista.test";

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
            ["Analista:ApiServerBaseUrl"] = AnalistaBaseUrl,
            ["GitHub:AppToken"] = "test-token",
            ["AzureDevOps:AppToken"] = "test-token"
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("Platform").ConfigurePrimaryHttpMessageHandler(() => Handler);
            services.AddHttpClient("Analista").ConfigurePrimaryHttpMessageHandler(() => Handler);
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

public sealed class SpecUsEndpointsIntegrationTests
{
    private const string SpecMarkdown = "# Checkout\n\n## User Story\n**Como** operador, **quero** publicar.\n\n## Criterios de aceite\n- [ ] Issue criada\n\n## WBS - Plano de implementacao\n1.1 Endpoint\n";
    private const string DorApproved = "{\"dor_atendido\": true, \"pendencias\": []}";
    private const string DorBlocked = "{\"dor_atendido\": false, \"pendencias\": [\"falta criterio X\"]}";

    private static HttpResponseMessage AnalistaResponse(string dorJson) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { choices = new[] { new { message = new { content = dorJson } } } })
    };

    private static HttpResponseMessage GitHubContentResponse(string markdown) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { content = Convert.ToBase64String(Encoding.UTF8.GetBytes(markdown)) })
    };

    private static async Task<HttpClient> AuthenticatedClient(SpecUsApplicationFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<long> CreateGitHubWorkspace(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform", specs_path = "" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt64();
    }

    private static async Task<long> CreateAzureDevOpsWorkspace(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "azure_devops", platform_ref = "org/project", specs_repo = "org/project/repo", specs_path = "" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt64();
    }

    private static async Task<int> PipelineInstanceCount(SpecUsApplicationFactory factory, long workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PipelineInstances.CountAsync(x => x.WorkspaceId == workspaceId);
    }

    [Fact]
    public async Task GitHubPublishSucceedsAndPersistsPipelineInstance()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.Host == "api.github.com" && r.RequestUri.AbsolutePath.Contains("/contents/"), _ => GitHubContentResponse(SpecMarkdown));
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse(DorApproved));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { number = 42 }) });

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        var response = await client.PostAsync($"/workspaces/{workspaceId}/specs/docs/spec.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var instance = body.GetProperty("pipeline_instance");
        Assert.Equal("42", instance.GetProperty("externalRef").GetString());
        Assert.Equal(1, await PipelineInstanceCount(factory, workspaceId));
    }

    [Fact]
    public async Task AzureDevOpsPublishSucceedsAndPersistsWorkItemIdAsExternalRef()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.Host == "dev.azure.com" && r.RequestUri.AbsolutePath.Contains("/items"), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { content = SpecMarkdown }) });
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse(DorApproved));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.Contains("/_apis/wit/workitems/"), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = 123 }) });

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateAzureDevOpsWorkspace(client);

        var response = await client.PostAsync($"/workspaces/{workspaceId}/specs/docs/spec.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("123", body.GetProperty("pipeline_instance").GetProperty("externalRef").GetString());
        Assert.Equal(1, await PipelineInstanceCount(factory, workspaceId));
    }

    [Fact]
    public async Task DorBlockedReturnsPendenciasAndDoesNotPublishOrPersist()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.Host == "api.github.com" && r.RequestUri.AbsolutePath.Contains("/contents/"), _ => GitHubContentResponse(SpecMarkdown));
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse(DorBlocked));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => throw new InvalidOperationException("must not publish when DoR blocks"));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        var response = await client.PostAsync($"/workspaces/{workspaceId}/specs/docs/spec.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("dor_atendido").GetBoolean());
        Assert.Equal("falta criterio X", body.GetProperty("pendencias")[0].GetString());
        Assert.Equal(0, await PipelineInstanceCount(factory, workspaceId));
    }

    [Fact]
    public async Task MalformedAnalistaBaseUrlReturnsBadGatewayNotServerError()
    {
        using var factory = new SpecUsApplicationFactory { AnalistaBaseUrl = "not a valid url" };
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.Host == "api.github.com" && r.RequestUri.AbsolutePath.Contains("/contents/"), _ => GitHubContentResponse(SpecMarkdown));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        var response = await client.PostAsync($"/workspaces/{workspaceId}/specs/docs/spec.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(0, await PipelineInstanceCount(factory, workspaceId));
    }

    [Fact]
    public async Task SpecFetchFailureReturnsBadGatewayAndDoesNotPersist()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.Host == "api.github.com" && r.RequestUri.AbsolutePath.Contains("/contents/"), _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        var response = await client.PostAsync($"/workspaces/{workspaceId}/specs/docs/spec.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(0, await PipelineInstanceCount(factory, workspaceId));
    }

    [Fact]
    public async Task GitHubIssueCreationFailureReturnsBadGatewayAndDoesNotPersist()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.Host == "api.github.com" && r.RequestUri.AbsolutePath.Contains("/contents/"), _ => GitHubContentResponse(SpecMarkdown));
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse(DorApproved));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        var response = await client.PostAsync($"/workspaces/{workspaceId}/specs/docs/spec.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(0, await PipelineInstanceCount(factory, workspaceId));
    }
}
