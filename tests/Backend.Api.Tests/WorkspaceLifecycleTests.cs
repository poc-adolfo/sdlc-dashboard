using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Backend.Api.Apis;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Tests;

/// <summary>
/// Covers the two seção-10 Gherkin scenarios that weren't exercised anywhere yet: "Editar plataforma de
/// um workspace com histórico é bloqueado" and "Arquivar workspace preserva histórico" (WBS item 15 -
/// real HTTP-level integration tests, not just the WorkspaceEndpoints handler code existing).
/// </summary>
public sealed class WorkspaceLifecycleTests
{
    private const string SpecMarkdown = "# Checkout\n\n> Status: rascunho (2026-08-05).\n\n## User Story\n**Como** operador, **quero** publicar.\n\n## Criterios de aceite\n- [ ] Issue criada\n\n## WBS - Plano de implementacao\n1.1 Endpoint\n";
    private const string DorApproved = "{\"dor_atendido\": true, \"pendencias\": []}";

    private static HttpResponseMessage AnalistaResponse(string dorJson) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { choices = new[] { new { message = new { content = dorJson } } } })
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
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private static async Task<long> SeedPipelineInstance(SpecUsApplicationFactory factory, long workspaceId, string externalRef = "1")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pipeline = new PipelineInstance { WorkspaceId = workspaceId, ExternalRef = externalRef, FaseAtual = PipelinePhase.Requisitos, GateStatus = GateStatus.Approved, CreatedAt = DateTime.UtcNow };
        db.PipelineInstances.Add(pipeline);
        await db.SaveChangesAsync();
        return pipeline.Id;
    }

    [Fact]
    public async Task EditingPlatformOrPlatformRefAfterAPipelineInstanceExistsIsBlocked()
    {
        using var factory = new SpecUsApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);
        await SeedPipelineInstance(factory, workspaceId);

        var changePlatform = await client.PatchAsJsonAsync($"/workspaces/{workspaceId}", new { platform = "azure_devops" });
        Assert.Equal(HttpStatusCode.Conflict, changePlatform.StatusCode);

        var changePlatformRef = await client.PatchAsJsonAsync($"/workspaces/{workspaceId}", new { platform_ref = "acme/other-repo" });
        Assert.Equal(HttpStatusCode.Conflict, changePlatformRef.StatusCode);

        // The block is scoped to platform/platform_ref specifically - other fields must still be
        // editable once a pipeline_instance exists.
        var renamed = await client.PatchAsJsonAsync($"/workspaces/{workspaceId}", new { name = "Renamed Workspace" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var workspace = await client.GetFromJsonAsync<JsonElement>($"/workspaces/{workspaceId}");
        Assert.Equal("github", workspace.GetProperty("platform").GetString());
        Assert.Equal("acme/platform", workspace.GetProperty("platformRef").GetString());
    }

    [Fact]
    public async Task EditingPlatformBeforeAnyPipelineInstanceExistsIsAllowed()
    {
        using var factory = new SpecUsApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        var response = await client.PatchAsJsonAsync($"/workspaces/{workspaceId}", new { platform_ref = "acme/renamed-repo" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var workspace = await client.GetFromJsonAsync<JsonElement>($"/workspaces/{workspaceId}");
        Assert.Equal("acme/renamed-repo", workspace.GetProperty("platformRef").GetString());
    }

    [Fact]
    public async Task ArchivingAWorkspacePreservesAssessmentSpecAndPipelineInstanceHistory()
    {
        using var factory = new SpecUsApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse(DorApproved));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { number = 55 }) });

        // Build up real history through the actual endpoints - not seeded directly - so this test
        // exercises the same HTTP contract an operator would.
        var assessment = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/assessments", new { client_name = "Acme", content = "notas do assessment" });
        assessment.EnsureSuccessStatusCode();
        var assessmentId = (await assessment.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        (await client.PostAsync($"/workspaces/{workspaceId}/assessments/{assessmentId}/concluir", null)).EnsureSuccessStatusCode();

        // Concluir sets workspace.client_id (from assessment.client_id) - that's the storage prefix key,
        // only known now, not at workspace-creation time above.
        var clientId = (await client.GetFromJsonAsync<JsonElement>($"/workspaces/{workspaceId}")).GetProperty("clientId").GetInt64();
        factory.Storage.Seed(clientId.ToString(), "checkout", "spec.md", SpecMarkdown);

        (await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs")).EnsureSuccessStatusCode(); // on-demand sync indexes spec.md
        var subirUs = await client.PostAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/spec.md/subir-us", content: null);
        Assert.Equal(HttpStatusCode.Created, subirUs.StatusCode);

        var archive = await client.PostAsync($"/workspaces/{workspaceId}/archive", null);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        var workspace = await client.GetFromJsonAsync<JsonElement>($"/workspaces/{workspaceId}");
        Assert.Equal("archived", workspace.GetProperty("status").GetString());

        var assessmentAfterArchive = await client.GetFromJsonAsync<JsonElement>($"/workspaces/{workspaceId}/assessments/{assessmentId}");
        Assert.Equal("concluido", assessmentAfterArchive.GetProperty("status").GetString());
        Assert.Equal("notas do assessment", assessmentAfterArchive.GetProperty("content").GetString());

        var specsAfterArchive = await client.GetFromJsonAsync<JsonElement>($"/workspaces/{workspaceId}/spec-projects/checkout/specs");
        Assert.Contains(specsAfterArchive.EnumerateArray(), s => s.GetProperty("fileName").GetString() == "spec.md");

        var dashboardAfterArchive = await client.GetFromJsonAsync<JsonElement>($"/workspaces/{workspaceId}/dashboard");
        Assert.Equal(1, dashboardAfterArchive.GetProperty("contagens").GetProperty("Requisitos").GetInt32());
    }
}
