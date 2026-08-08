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

/// <summary>A captured outbound call, kept for assertions on the request contract (URL, headers, body) the production code sent.</summary>
public sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body, System.Net.Http.Headers.HttpRequestHeaders Headers, System.Net.Http.Headers.HttpContentHeaders? ContentHeaders);

/// <summary>Routes fake HTTP responses by predicate; used to stand in for GitHub/Azure DevOps/Analista over HTTP in tests.</summary>
public sealed class RoutingFakeHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = new();
    public List<CapturedRequest> Captured { get; } = new();

    public void On(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond) => _routes.Add((match, respond));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Captured.Add(new CapturedRequest(request.Method, request.RequestUri!, body, request.Headers, request.Content?.Headers));

        foreach (var (match, respond) in _routes)
        {
            if (match(request)) return respond(request);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"no fake route registered for {request.Method} {request.RequestUri}")
        };
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
            ["Analista:AllowedHost"] = "analista.test",
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

        var fetchCall = Assert.Single(factory.Handler.Captured, c => c.Uri.Host == "api.github.com" && c.Method == HttpMethod.Get);
        Assert.Equal("/repos/acme/platform/contents/docs/spec.md", fetchCall.Uri.AbsolutePath);
        Assert.Equal("Bearer", fetchCall.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", fetchCall.Headers.Authorization?.Parameter);

        var issueCall = Assert.Single(factory.Handler.Captured, c => c.Uri.AbsolutePath.EndsWith("/issues"));
        Assert.Equal("/repos/acme/platform/issues", issueCall.Uri.AbsolutePath);
        Assert.Equal("Bearer", issueCall.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", issueCall.Headers.Authorization?.Parameter);
        var issuePayload = JsonDocument.Parse(issueCall.Body!).RootElement;
        Assert.Equal("US: Checkout", issuePayload.GetProperty("title").GetString());
        var issueBody = issuePayload.GetProperty("body").GetString()!;
        Assert.Contains("## User Story", issueBody);
        Assert.Contains("**Como** operador, **quero** publicar.", issueBody);
        Assert.Contains("## Criterios de aceite", issueBody);
        Assert.Contains("- [ ] Issue criada", issueBody);
        Assert.Contains("## WBS - Plano de implementacao", issueBody);
        Assert.Contains("1.1 Endpoint", issueBody);
        Assert.DoesNotContain("<details>", issueBody);

        var dorCall = Assert.Single(factory.Handler.Captured, c => c.Uri.Host == "analista.test");
        var dorPayload = JsonDocument.Parse(dorCall.Body!).RootElement;
        Assert.Equal(SpecMarkdown, dorPayload.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GitHubPublishAppliesTheSdlcPipelineLabelAndRecordsASpecPublication()
    {
        // Security review on PR #17: ReconciliationPollerService no longer trusts anything read back
        // from GitHub (an Issue's label/body are writable by any repo collaborator) - it trusts only a
        // spec_publication row, which only this session-authenticated endpoint can create
        // (ReconciliationPollerServiceTests). This is the other half of that contract: "Subir US" must
        // actually write that row when it publishes.
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.Host == "api.github.com" && r.RequestUri.AbsolutePath.Contains("/contents/"), _ => GitHubContentResponse(SpecMarkdown));
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse(DorApproved));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { number = 42 }) });

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        var response = await client.PostAsync($"/workspaces/{workspaceId}/specs/docs/spec.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var issueCall = Assert.Single(factory.Handler.Captured, c => c.Uri.AbsolutePath.EndsWith("/issues"));
        var issuePayload = JsonDocument.Parse(issueCall.Body!).RootElement;
        var labels = issuePayload.GetProperty("labels").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("sdlc-pipeline", labels);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var publication = Assert.Single(db.SpecPublications.Where(p => p.WorkspaceId == workspaceId));
        Assert.Equal("42", publication.ExternalRef);
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

        var fetchCall = Assert.Single(factory.Handler.Captured, c => c.Uri.Host == "dev.azure.com" && c.Method == HttpMethod.Get);
        Assert.Equal("/org/project/_apis/git/repositories/repo/items", fetchCall.Uri.AbsolutePath);
        Assert.Contains("path=/docs/spec.md", fetchCall.Uri.Query);
        Assert.Equal("Basic", fetchCall.Headers.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes(":test-token")), fetchCall.Headers.Authorization?.Parameter);

        var workItemCall = Assert.Single(factory.Handler.Captured, c => c.Uri.AbsolutePath.Contains("/_apis/wit/workitems/"));
        Assert.Equal("/org/project/_apis/wit/workitems/$User Story", Uri.UnescapeDataString(workItemCall.Uri.AbsolutePath));
        Assert.Equal("application/json-patch+json", workItemCall.ContentHeaders?.ContentType?.MediaType);
        var patch = JsonDocument.Parse(workItemCall.Body!).RootElement;
        Assert.Equal("US: Checkout", patch[0].GetProperty("value").GetString());
        Assert.Equal("/fields/System.Title", patch[0].GetProperty("path").GetString());
        var descriptionHtml = patch[1].GetProperty("value").GetString()!;
        Assert.Equal("/fields/System.Description", patch[1].GetProperty("path").GetString());
        Assert.Contains("<h2>User Story</h2>", descriptionHtml);
        Assert.Contains("<li>[ ] Issue criada</li>", descriptionHtml);
        Assert.Contains("<h2>WBS - Plano de implementacao</h2>", descriptionHtml);
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

    [Fact]
    public async Task ConfiguredSpecsPathIsPrependedToTheRequestedPath()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.Host == "api.github.com" && r.RequestUri.AbsolutePath.Contains("/contents/"), _ => GitHubContentResponse(SpecMarkdown));
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse(DorApproved));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { number = 1 }) });

        using var client = await AuthenticatedClient(factory);
        var create = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform", specs_path = "specs/" });
        create.EnsureSuccessStatusCode();
        var workspaceId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var response = await client.PostAsync($"/workspaces/{workspaceId}/specs/foo.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var fetchCall = Assert.Single(factory.Handler.Captured, c => c.Uri.Host == "api.github.com" && c.Method == HttpMethod.Get);
        Assert.Equal("/repos/acme/platform/contents/specs/foo.md", fetchCall.Uri.AbsolutePath);
    }

    [Fact]
    public async Task PublishLinksSpecIdWhenTheSpecWasPreviouslyListedWithAConfiguredSpecsPath()
    {
        // Regression test: the spec index (SpecListingEndpoints) always stores the full repo-relative
        // path ("specs/foo.md"), while /subir-us's route only carries the part after specs_path
        // ("foo.md") - the lookup used to compare against the route segment instead of the same full
        // path used by the index, so SpecId never linked whenever specs_path was configured.
        using var factory = new SpecUsApplicationFactory();
        const string specWithStatus = "# Checkout\n\n> Status: rascunho (2026-08-05).\n\n## User Story\n**Como** operador, **quero** publicar.\n\n## Criterios de aceite\n- [ ] Issue criada\n\n## WBS - Plano de implementacao\n1.1 Endpoint\n";
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.Host == "api.github.com" && r.RequestUri.AbsolutePath == "/repos/acme/platform/contents/specs", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new { name = "foo.md", path = "specs/foo.md", type = "file" } }) });
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.Host == "api.github.com" && r.RequestUri.AbsolutePath.Contains("/contents/"), _ => GitHubContentResponse(specWithStatus));
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse(DorApproved));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { number = 1 }) });

        using var client = await AuthenticatedClient(factory);
        var create = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform", specs_path = "specs/" });
        create.EnsureSuccessStatusCode();
        var workspaceId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var listing = await client.GetAsync($"/workspaces/{workspaceId}/specs");
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        var listedSpecId = (await listing.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("path").GetString();
        Assert.Equal("specs/foo.md", listedSpecId);

        var response = await client.PostAsync($"/workspaces/{workspaceId}/specs/foo.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("pipeline_instance").TryGetProperty("specId", out var specIdElement));
        Assert.NotEqual(JsonValueKind.Null, specIdElement.ValueKind);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var spec = await db.Specs.SingleAsync(s => s.WorkspaceId == workspaceId && s.Path == "specs/foo.md");
        Assert.Equal(spec.Id, specIdElement.GetInt64());
    }

    [Fact]
    public async Task SpecsRepoDifferentFromPlatformRefFetchesFromSpecsRepoAndAttachesFullSpecToIssue()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.Host == "api.github.com" && r.RequestUri.AbsolutePath.Contains("/contents/"), _ => GitHubContentResponse(SpecMarkdown));
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse(DorApproved));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { number = 7 }) });

        using var client = await AuthenticatedClient(factory);
        var create = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform", specs_repo = "acme/specs-repo", specs_path = "" });
        create.EnsureSuccessStatusCode();
        var workspaceId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var response = await client.PostAsync($"/workspaces/{workspaceId}/specs/docs/spec.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var fetchCall = Assert.Single(factory.Handler.Captured, c => c.Uri.Host == "api.github.com" && c.Method == HttpMethod.Get);
        Assert.Equal("/repos/acme/specs-repo/contents/docs/spec.md", fetchCall.Uri.AbsolutePath);

        var issueCall = Assert.Single(factory.Handler.Captured, c => c.Uri.AbsolutePath.EndsWith("/issues"));
        Assert.Equal("/repos/acme/platform/issues", issueCall.Uri.AbsolutePath);
        var issueBody = JsonDocument.Parse(issueCall.Body!).RootElement.GetProperty("body").GetString()!;
        Assert.Contains("<details>", issueBody);
        Assert.Contains("Spec completa: docs/spec.md", issueBody);
        Assert.Contains("```markdown\n" + SpecMarkdown, issueBody);
    }

    [Fact]
    public async Task AzureDevOpsRepoWithoutThreeSegmentsReturnsBadGateway()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse(DorApproved));

        using var client = await AuthenticatedClient(factory);
        var create = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "azure_devops", platform_ref = "org/project", specs_path = "" });
        create.EnsureSuccessStatusCode();
        var workspaceId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var response = await client.PostAsync($"/workspaces/{workspaceId}/specs/docs/spec.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(0, await PipelineInstanceCount(factory, workspaceId));
        Assert.DoesNotContain(factory.Handler.Captured, c => c.Uri.Host == "analista.test");
    }

    [Fact]
    public async Task SpecWithoutTitleHeadingFallsBackToDefaultTitle()
    {
        const string specWithoutHeading = "## User Story\n**Como** operador, **quero** publicar.\n\n## Criterios de aceite\n- [ ] ok\n\n## WBS - Plano de implementacao\n1.1 Endpoint\n";
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.Host == "api.github.com" && r.RequestUri.AbsolutePath.Contains("/contents/"), _ => GitHubContentResponse(specWithoutHeading));
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse(DorApproved));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { number = 9 }) });

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        var response = await client.PostAsync($"/workspaces/{workspaceId}/specs/docs/spec.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var issueCall = Assert.Single(factory.Handler.Captured, c => c.Uri.AbsolutePath.EndsWith("/issues"));
        var title = JsonDocument.Parse(issueCall.Body!).RootElement.GetProperty("title").GetString();
        Assert.Equal("US: Sem titulo", title);
    }

    [Fact]
    public async Task AzureDevOpsFetchResponseWithoutContentPropertyFallsBackToRawBody()
    {
        const string rawBody = "{\"unexpected\":\"shape\"}";
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.Host == "dev.azure.com" && r.RequestUri.AbsolutePath.Contains("/items"), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(rawBody, Encoding.UTF8, "application/json") });
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse(DorApproved));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.Contains("/_apis/wit/workitems/"), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = 55 }) });

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateAzureDevOpsWorkspace(client);

        var response = await client.PostAsync($"/workspaces/{workspaceId}/specs/docs/spec.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dorCall = Assert.Single(factory.Handler.Captured, c => c.Uri.Host == "analista.test");
        Assert.Equal(rawBody, JsonDocument.Parse(dorCall.Body!).RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }
}
