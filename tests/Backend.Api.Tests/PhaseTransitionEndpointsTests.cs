using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Tests;

public sealed class PhaseTransitionEndpointsTests
{
    private static async Task<HttpClient> AuthenticatedClient(CredentialApplicationFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<long> CreateWorkspace(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private static async Task<long> SeedPipelineInstance(CredentialApplicationFactory factory, long workspaceId, PipelinePhase fase = PipelinePhase.Requisitos)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pipeline = new PipelineInstance { WorkspaceId = workspaceId, ExternalRef = "7", FaseAtual = fase, GateStatus = GateStatus.Approved, CreatedAt = DateTime.UtcNow };
        db.PipelineInstances.Add(pipeline);
        await db.SaveChangesAsync();
        return pipeline.Id;
    }

    private static async Task<PipelineInstance> LoadPipeline(CredentialApplicationFactory factory, long id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PipelineInstances.FindAsync(id) ?? throw new InvalidOperationException("not found");
    }

    private static Task<int> PhaseTransitionCount(CredentialApplicationFactory factory, long pipelineInstanceId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return Task.FromResult(db.PhaseTransitions.Count(t => t.PipelineInstanceId == pipelineInstanceId));
    }

    [Theory]
    [InlineData("design", PipelinePhase.Design)]
    [InlineData("Dev", PipelinePhase.Dev)]
    [InlineData("DEV", PipelinePhase.Dev)]
    public async Task ReportsAValidTransitionAndAdvancesFase(string fase, PipelinePhase expected)
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspace(client);
        var pipelineId = await SeedPipelineInstance(factory, workspaceId);

        var response = await client.PostAsJsonAsync($"/pipeline-instances/{pipelineId}/phase-transitions", new { fase, source_event = "reported.test" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var pipeline = await LoadPipeline(factory, pipelineId);
        Assert.Equal(expected, pipeline.FaseAtual);
        Assert.Equal(1, await PhaseTransitionCount(factory, pipelineId));
    }

    [Fact]
    public async Task MissingPipelineInstanceReturnsNotFound()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);

        var response = await client.PostAsJsonAsync("/pipeline-instances/999999/phase-transitions", new { fase = "design", source_event = "reported.test" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("code_review")]
    [InlineData("qa")]
    [InlineData("deploy")]
    [InlineData("")]
    [InlineData(null)]
    public async Task InvalidFaseIsRejected(string? fase)
    {
        // seção 6.1: only Design/Dev are ever "reported" - every other phase is inferred automatically
        // from a webhook or reconciliation, so this endpoint must not accept them as a bypass.
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspace(client);
        var pipelineId = await SeedPipelineInstance(factory, workspaceId);

        var response = await client.PostAsJsonAsync($"/pipeline-instances/{pipelineId}/phase-transitions", new { fase, source_event = "reported.test" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(PipelinePhase.Requisitos, (await LoadPipeline(factory, pipelineId)).FaseAtual);
    }

    [Fact]
    public async Task MissingSourceEventIsRejected()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspace(client);
        var pipelineId = await SeedPipelineInstance(factory, workspaceId);

        var response = await client.PostAsJsonAsync($"/pipeline-instances/{pipelineId}/phase-transitions", new { fase = "design" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task NeverRegressesFaseAlreadyPastTheReportedOne()
    {
        // A late "reported" call (Dev dispatch confirmation arriving after the real PR was already
        // opened and moved fase_atual to Code Review) must be a harmless no-op, not a regression.
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspace(client);
        var pipelineId = await SeedPipelineInstance(factory, workspaceId, fase: PipelinePhase.CodeReview);

        var response = await client.PostAsJsonAsync($"/pipeline-instances/{pipelineId}/phase-transitions", new { fase = "design", source_event = "reported.late" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(PipelinePhase.CodeReview, (await LoadPipeline(factory, pipelineId)).FaseAtual);
        Assert.Equal(0, await PhaseTransitionCount(factory, pipelineId));
    }

    [Fact]
    public async Task RepeatedIdenticalReportIsIdempotent()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspace(client);
        var pipelineId = await SeedPipelineInstance(factory, workspaceId);

        for (var i = 0; i < 2; i++)
        {
            var response = await client.PostAsJsonAsync($"/pipeline-instances/{pipelineId}/phase-transitions", new { fase = "design", source_event = "reported.test" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Assert.Equal(PipelinePhase.Design, (await LoadPipeline(factory, pipelineId)).FaseAtual);
        Assert.Equal(1, await PhaseTransitionCount(factory, pipelineId));
    }

    [Fact]
    public async Task RequiresASessionCookie()
    {
        using var factory = new CredentialApplicationFactory();
        using var anonymousClient = factory.CreateClient();

        var response = await anonymousClient.PostAsJsonAsync("/pipeline-instances/1/phase-transitions", new { fase = "design", source_event = "reported.test" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
