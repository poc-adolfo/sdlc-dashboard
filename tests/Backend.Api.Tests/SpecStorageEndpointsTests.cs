using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Api.Apis;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Tests;

public sealed class SpecStorageEndpointsTests
{
    private const string RascunhoSpec = "# Rascunho spec\n\n> Status: rascunho (2026-08-05).\n\nConteudo.\n";
    private const string PropostaSpec = "# Proposta spec\n\n> Status: proposta (2026-08-05).\n\nConteudo.\n";

    private static async Task<HttpClient> AuthenticatedClient(SpecUsApplicationFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<long> SeedClient(SpecUsApplicationFactory factory, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = new Client { Name = name, CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static async Task<long> CreateGitHubWorkspace(HttpClient client, long clientId)
    {
        var response = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform", client_id = clientId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task ListProjectsReturnsProjectsCreatedForTheWorkspacesClient()
    {
        using var factory = new SpecUsApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var clientId = await SeedClient(factory, "Acme");
        var workspaceId = await CreateGitHubWorkspace(client, clientId);

        var create = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/spec-projects", new { name = "checkout" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var listing = await client.GetFromJsonAsync<JsonElement>($"/workspaces/{workspaceId}/spec-projects");
        Assert.Equal(1, listing.GetArrayLength());
        Assert.Equal("checkout", listing[0].GetString());
    }

    [Fact]
    public async Task CreatingASpecFileImplicitlyCreatesItsProjectToo()
    {
        using var factory = new SpecUsApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var clientId = await SeedClient(factory, "Acme");
        var workspaceId = await CreateGitHubWorkspace(client, clientId);

        var save = await client.PutAsJsonAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/spec.md", new { content = RascunhoSpec });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var projects = await client.GetFromJsonAsync<JsonElement>($"/workspaces/{workspaceId}/spec-projects");
        Assert.Equal(1, projects.GetArrayLength());
        Assert.Equal("checkout", projects[0].GetString());
    }

    [Fact]
    public async Task RejectsAProjectNameThatWouldEscapeTheClientPrefix()
    {
        using var factory = new SpecUsApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var clientId = await SeedClient(factory, "Acme");
        var workspaceId = await CreateGitHubWorkspace(client, clientId);

        var create = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/spec-projects", new { name = "../other-client" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
    }

    [Fact]
    public async Task GetAndSaveRoundTripContent()
    {
        using var factory = new SpecUsApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var clientId = await SeedClient(factory, "Acme");
        var workspaceId = await CreateGitHubWorkspace(client, clientId);

        var save = await client.PutAsJsonAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/spec.md", new { content = RascunhoSpec });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var read = await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/spec.md");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(RascunhoSpec, await read.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GettingAFileThatDoesNotExistReturnsNotFound()
    {
        using var factory = new SpecUsApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var clientId = await SeedClient(factory, "Acme");
        var workspaceId = await CreateGitHubWorkspace(client, clientId);

        var response = await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/missing.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListingSpecsSyncsTheIndexWithStatusAndTitleParsedFromContent()
    {
        using var factory = new SpecUsApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var clientId = await SeedClient(factory, "Acme");
        var workspaceId = await CreateGitHubWorkspace(client, clientId);
        factory.Storage.Seed(clientId.ToString(), "checkout", "rascunho.md", RascunhoSpec);
        factory.Storage.Seed(clientId.ToString(), "checkout", "proposta.md", PropostaSpec);

        var response = await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, items.GetArrayLength());
        var rascunho = items.EnumerateArray().Single(i => i.GetProperty("fileName").GetString() == "rascunho.md");
        Assert.Equal("Rascunho spec", rascunho.GetProperty("title").GetString());
        Assert.Equal("rascunho", rascunho.GetProperty("status").GetString());
        Assert.Equal(1, rascunho.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task RepeatedListingDoesNotDuplicateSpecRowsAndBumpsVersionOnChange()
    {
        using var factory = new SpecUsApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var clientId = await SeedClient(factory, "Acme");
        var workspaceId = await CreateGitHubWorkspace(client, clientId);
        factory.Storage.Seed(clientId.ToString(), "checkout", "spec.md", RascunhoSpec);

        var first = await (await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, first.GetArrayLength());
        Assert.Equal(1, first[0].GetProperty("version").GetInt32());

        factory.Storage.Seed(clientId.ToString(), "checkout", "spec.md", PropostaSpec);
        var second = await (await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, second.GetArrayLength());
        Assert.Equal("proposta", second[0].GetProperty("status").GetString());
        Assert.Equal(2, second[0].GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task StaleSpecIsRemovedWhenFileDisappearsFromStorage()
    {
        using var factory = new SpecUsApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var clientId = await SeedClient(factory, "Acme");
        var workspaceId = await CreateGitHubWorkspace(client, clientId);
        factory.Storage.Seed(clientId.ToString(), "checkout", "a.md", RascunhoSpec);
        factory.Storage.Seed(clientId.ToString(), "checkout", "b.md", PropostaSpec);

        var first = await (await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, first.GetArrayLength());

        factory.Storage.RemoveFile(clientId.ToString(), "checkout", "b.md");
        var second = await (await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, second.GetArrayLength());
        Assert.Equal("a.md", second[0].GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task StaleSpecReferencedByAPipelineInstanceIsNotRemoved()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { choices = new[] { new { message = new { content = "{\"dor_atendido\": true, \"pendencias\": []}" } } } })
        });
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { number = 1 }) });

        using var client = await AuthenticatedClient(factory);
        var clientId = await SeedClient(factory, "Acme");
        var workspaceId = await CreateGitHubWorkspace(client, clientId);
        factory.Storage.Seed(clientId.ToString(), "checkout", "a.md", RascunhoSpec);

        await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs"); // syncs the spec index
        var publish = await client.PostAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/a.md/subir-us", content: null);
        Assert.Equal(HttpStatusCode.Created, publish.StatusCode);

        factory.Storage.RemoveFile(clientId.ToString(), "checkout", "a.md");
        var afterRemoval = await (await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, afterRemoval.GetArrayLength()); // gone from the *listing* (file no longer in storage)...

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Specs.CountAsync(s => s.WorkspaceId == workspaceId)); // ...but the index row survives, still referenced by pipeline_instance
    }

    [Fact]
    public async Task MissingWorkspaceReturnsNotFound()
    {
        using var factory = new SpecUsApplicationFactory();
        using var client = await AuthenticatedClient(factory);

        var response = await client.GetAsync("/workspaces/999999/spec-projects");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WorkspaceWithoutClientIdReturns422OnEveryRoute()
    {
        using var factory = new SpecUsApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var create = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform" });
        create.EnsureSuccessStatusCode();
        var workspaceId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"/workspaces/{workspaceId}/spec-projects")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync($"/workspaces/{workspaceId}/spec-projects", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/x/specs")).StatusCode);
    }

    [Fact]
    public async Task ChatProxiesToTheSpecsSkillAndReturnsTheReply()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { choices = new[] { new { message = new { content = "Claro, aqui está uma sugestão de seção de riscos." } } } })
        });

        using var client = await AuthenticatedClient(factory);
        var clientId = await SeedClient(factory, "Acme");
        var workspaceId = await CreateGitHubWorkspace(client, clientId);

        var response = await client.PostAsJsonAsync(
            $"/workspaces/{workspaceId}/spec-projects/checkout/specs/spec.md/chat",
            new { messages = new[] { new { role = "user", content = "Sugira uma seção de riscos." } } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Claro, aqui está uma sugestão de seção de riscos.", body.GetProperty("reply").GetString());

        var chatCall = Assert.Single(factory.Handler.Captured, c => c.Uri.Host == "analista.test");
        var payload = JsonDocument.Parse(chatCall.Body!).RootElement;
        Assert.Equal("user", payload.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Sugira uma seção de riscos.", payload.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ChatFailureReturnsBadGateway()
    {
        using var factory = new SpecUsApplicationFactory();
        // No route registered for analista.test - RoutingFakeHandler's fallback 404s.

        using var client = await AuthenticatedClient(factory);
        var clientId = await SeedClient(factory, "Acme");
        var workspaceId = await CreateGitHubWorkspace(client, clientId);

        var response = await client.PostAsJsonAsync(
            $"/workspaces/{workspaceId}/spec-projects/checkout/specs/spec.md/chat",
            new { messages = new[] { new { role = "user", content = "oi" } } });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task ChatDoesNotWriteToStorage()
    {
        // "Uma caixa como OpenWebUI" (spec 2026-08-09 update) is a conversation, not an auto-apply editor
        // - the operator decides what to keep via the separate content editor/PUT.
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { choices = new[] { new { message = new { content = "# Nova versão inteira da spec" } } } })
        });

        using var client = await AuthenticatedClient(factory);
        var clientId = await SeedClient(factory, "Acme");
        var workspaceId = await CreateGitHubWorkspace(client, clientId);
        factory.Storage.Seed(clientId.ToString(), "checkout", "spec.md", RascunhoSpec);

        await client.PostAsJsonAsync(
            $"/workspaces/{workspaceId}/spec-projects/checkout/specs/spec.md/chat",
            new { messages = new[] { new { role = "user", content = "reescreva tudo" } } });

        var stillOriginal = await client.GetStringAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/spec.md");
        Assert.Equal(RascunhoSpec, stillOriginal);
    }
}

public sealed class SpecStorageParsingTests
{
    [Theory]
    [InlineData("> Status: rascunho (2026-08-05).", "rascunho")]
    [InlineData("> Status: Rascunho (2026-08-05).", "rascunho")]
    [InlineData("> Status: proposta (2026-08-05).", "proposta")]
    [InlineData("> Status:   implementado   (2026-08-05).", "implementado")]
    public void ParseStatusNormalizesCaseAndWhitespace(string blockquote, string expected)
    {
        Assert.Equal(expected, SpecStorageEndpoints.ParseStatus($"# Titulo\n\n{blockquote}\n"));
    }

    [Fact]
    public void ParseStatusNormalizesAccents()
    {
        Assert.Equal("rascunho", SpecStorageEndpoints.ParseStatus("> Status: Rascunho (2026-08-05).\ncontent with çedilha and accents áéí"));
    }

    [Fact]
    public void ParseStatusReturnsNullWhenBlockquoteIsMissing()
    {
        Assert.Null(SpecStorageEndpoints.ParseStatus("# Titulo\n\nSem blockquote de status."));
    }

    [Fact]
    public void ParseTitleUsesFirstHeading()
    {
        Assert.Equal("Meu Titulo", SpecStorageEndpoints.ParseTitle("# Meu Titulo\n\n> Status: rascunho (2026-08-05).", "x.md"));
    }

    [Fact]
    public void ParseTitleFallsBackToFileNameWhenHeadingIsMissing()
    {
        Assert.Equal("x", SpecStorageEndpoints.ParseTitle("> Status: rascunho (2026-08-05).", "x.md"));
    }
}
