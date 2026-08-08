using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Tests;

public sealed class DashboardEndpointsTests
{
    private static async Task<HttpClient> AuthenticatedClient(CredentialApplicationFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<long> CreateGitHubWorkspace(HttpClient client, string platformRef = "acme/platform")
    {
        var response = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = platformRef });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private static async Task<long> CreateAzureDevOpsWorkspace(HttpClient client, string? codeRepo = "org/project/repo")
    {
        var response = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "azure_devops", platform_ref = "org/project", code_repo = codeRepo });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private static async Task SeedPipelineInstance(CredentialApplicationFactory factory, long workspaceId, string externalRef, PipelinePhase fase, GateStatus gateStatus, string? prRef = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PipelineInstances.Add(new PipelineInstance { WorkspaceId = workspaceId, ExternalRef = externalRef, FaseAtual = fase, GateStatus = gateStatus, PrRef = prRef, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ReturnsAggregateCountsPerFaseForTheWorkspaceOnly()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);
        var otherWorkspaceId = await CreateGitHubWorkspace(client, "acme/other");

        await SeedPipelineInstance(factory, workspaceId, "1", PipelinePhase.Requisitos, GateStatus.Approved);
        await SeedPipelineInstance(factory, workspaceId, "2", PipelinePhase.Requisitos, GateStatus.Approved);
        await SeedPipelineInstance(factory, workspaceId, "3", PipelinePhase.Dev, GateStatus.Approved);
        await SeedPipelineInstance(factory, otherWorkspaceId, "1", PipelinePhase.Requisitos, GateStatus.Approved); // must not leak into workspaceId's counts

        var response = await client.GetAsync($"/workspaces/{workspaceId}/dashboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var contagens = body.GetProperty("contagens");
        Assert.Equal(2, contagens.GetProperty("Requisitos").GetInt32());
        Assert.Equal(1, contagens.GetProperty("Dev").GetInt32());
        Assert.Equal(0, contagens.GetProperty("Design").GetInt32());
        Assert.Equal(0, contagens.GetProperty("Deploy").GetInt32());
    }

    [Fact]
    public async Task GatesPendentesOnlyIncludesCodeReviewQaAndSeguranca()
    {
        // seção 6.5: Design/Dev never show a pending gate (reported phases, no PR to review yet),
        // Requisitos is auto-approved, Deploy has no next platform gate - even if some of these somehow
        // carry GateStatus.Pending, they must not surface in gates_pendentes.
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        await SeedPipelineInstance(factory, workspaceId, "1", PipelinePhase.Requisitos, GateStatus.Pending);
        await SeedPipelineInstance(factory, workspaceId, "2", PipelinePhase.Design, GateStatus.Pending);
        await SeedPipelineInstance(factory, workspaceId, "3", PipelinePhase.Dev, GateStatus.Pending);
        await SeedPipelineInstance(factory, workspaceId, "4", PipelinePhase.Deploy, GateStatus.Approved);
        await SeedPipelineInstance(factory, workspaceId, "5", PipelinePhase.CodeReview, GateStatus.Approved); // pending only, not approved
        await SeedPipelineInstance(factory, workspaceId, "6", PipelinePhase.CodeReview, GateStatus.Pending, prRef: "42");

        var response = await client.GetAsync($"/workspaces/{workspaceId}/dashboard");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var gates = body.GetProperty("gates_pendentes").EnumerateArray().ToList();
        var gate = Assert.Single(gates);
        Assert.Equal("6", gate.GetProperty("external_ref").GetString());
        Assert.Equal("CodeReview", gate.GetProperty("fase_atual").GetString());
        Assert.Equal("Code Review → QA", gate.GetProperty("transicao").GetString());
        Assert.Equal("Reviewer designado", gate.GetProperty("aprovador_esperado").GetString());
    }

    [Theory]
    [InlineData(PipelinePhase.CodeReview, "Code Review → QA", "Reviewer designado")]
    [InlineData(PipelinePhase.Qa, "QA → Segurança", "QA Lead")]
    [InlineData(PipelinePhase.Seguranca, "Segurança → Deploy", "AppSec + Release Manager")]
    public async Task EachPendingGateReportsTheApproverFromTable43(PipelinePhase fase, string transicao, string aprovador)
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);
        await SeedPipelineInstance(factory, workspaceId, "1", fase, GateStatus.Pending, prRef: "42");

        var response = await client.GetAsync($"/workspaces/{workspaceId}/dashboard");

        var gate = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gates_pendentes").EnumerateArray());
        Assert.Equal(transicao, gate.GetProperty("transicao").GetString());
        Assert.Equal(aprovador, gate.GetProperty("aprovador_esperado").GetString());
    }

    [Fact]
    public async Task BuildsAGitHubDeepLinkFromPlatformRefAndPrRef()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client, "acme/platform");
        await SeedPipelineInstance(factory, workspaceId, "1", PipelinePhase.CodeReview, GateStatus.Pending, prRef: "42");

        var response = await client.GetAsync($"/workspaces/{workspaceId}/dashboard");

        var gate = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gates_pendentes").EnumerateArray());
        Assert.Equal("https://github.com/acme/platform/pull/42", gate.GetProperty("deep_link").GetString());
    }

    [Fact]
    public async Task BuildsAnAzureDevOpsDeepLinkFromCodeRepoAndPrRef()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateAzureDevOpsWorkspace(client, "org/project/repo");
        await SeedPipelineInstance(factory, workspaceId, "1", PipelinePhase.CodeReview, GateStatus.Pending, prRef: "99");

        var response = await client.GetAsync($"/workspaces/{workspaceId}/dashboard");

        var gate = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gates_pendentes").EnumerateArray());
        Assert.Equal("https://dev.azure.com/org/project/_git/repo/pullrequest/99", gate.GetProperty("deep_link").GetString());
    }

    [Fact]
    public async Task OmitsDeepLinkWhenAzureDevOpsCodeRepoIsMissing()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateAzureDevOpsWorkspace(client, codeRepo: null);
        await SeedPipelineInstance(factory, workspaceId, "1", PipelinePhase.CodeReview, GateStatus.Pending, prRef: "99");

        var response = await client.GetAsync($"/workspaces/{workspaceId}/dashboard");

        var gate = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gates_pendentes").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, gate.GetProperty("deep_link").ValueKind);
    }

    [Fact]
    public async Task OmitsDeepLinkWhenPrRefIsNotSetYet()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);
        await SeedPipelineInstance(factory, workspaceId, "1", PipelinePhase.CodeReview, GateStatus.Pending, prRef: null);

        var response = await client.GetAsync($"/workspaces/{workspaceId}/dashboard");

        var gate = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gates_pendentes").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, gate.GetProperty("deep_link").ValueKind);
    }

    [Fact]
    public async Task MissingWorkspaceReturnsNotFound()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);

        var response = await client.GetAsync("/workspaces/999999/dashboard");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RequiresASessionCookie()
    {
        using var factory = new CredentialApplicationFactory();
        using var anonymousClient = factory.CreateClient();

        var response = await anonymousClient.GetAsync("/workspaces/1/dashboard");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
