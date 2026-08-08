using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Api.Services;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Api.Tests;

public sealed class ReconciliationApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"reconcile-tests-{Guid.NewGuid():N}.db");
    public RoutingFakeHandler Handler { get; } = new();
    public FakeSecretStore Secrets { get; } = new();

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
            ["GitHub:AppToken"] = "test-token"
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ISecretStore>(Secrets);
            services.AddHttpClient("Platform").ConfigurePrimaryHttpMessageHandler(() => Handler);
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

public sealed class ReconciliationPollerServiceTests
{
    private static async Task<HttpClient> AuthenticatedClient(ReconciliationApplicationFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<long> CreateGitHubWorkspace(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private static async Task RegisterCredential(HttpClient client, long workspaceId, string perfil, string platformUsername)
    {
        var response = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/credenciais", new { perfil, platform_username = platformUsername, token = "irrelevant" });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<long> SeedPipelineInstance(ReconciliationApplicationFactory factory, long workspaceId, string externalRef, PipelinePhase fase = PipelinePhase.Requisitos, string? prRef = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pipeline = new PipelineInstance { WorkspaceId = workspaceId, ExternalRef = externalRef, FaseAtual = fase, PrRef = prRef, GateStatus = GateStatus.Approved, CreatedAt = DateTime.UtcNow };
        db.PipelineInstances.Add(pipeline);
        await db.SaveChangesAsync();
        return pipeline.Id;
    }

    private static ReconciliationPollerService BuildService(ReconciliationApplicationFactory factory, int intervalMinutes = 5)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Reconciliation:IntervalMinutes"] = intervalMinutes.ToString() }).Build();
        return new ReconciliationPollerService(factory.Services.GetRequiredService<IServiceScopeFactory>(), config, NullLogger<ReconciliationPollerService>.Instance);
    }

    private static Task<List<PipelineInstance>> LoadPipelines(ReconciliationApplicationFactory factory, long workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return Task.FromResult(db.PipelineInstances.Where(p => p.WorkspaceId == workspaceId).ToList());
    }

    [Fact]
    public async Task RecreatesMissingPipelineInstanceForALabeledIssue()
    {
        using var factory = new ReconciliationApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient);

        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/issues", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new { number = 7, body = "US body" } }) });

        var service = BuildService(factory);
        await service.RunOnceAsync(CancellationToken.None);

        var pipelines = await LoadPipelines(factory, workspaceId);
        var created = Assert.Single(pipelines);
        Assert.Equal("7", created.ExternalRef);
        Assert.Equal(PipelinePhase.Requisitos, created.FaseAtual);
    }

    [Fact]
    public async Task DoesNotDuplicateAnAlreadyExistingPipelineInstance()
    {
        using var factory = new ReconciliationApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient);
        await SeedPipelineInstance(factory, workspaceId, externalRef: "7");

        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/issues", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new { number = 7, body = "US body" } }) });

        var service = BuildService(factory);
        await service.RunOnceAsync(CancellationToken.None);

        Assert.Single(await LoadPipelines(factory, workspaceId));
    }

    [Fact]
    public async Task IgnoresIssuesThatAreActuallyPullRequests()
    {
        using var factory = new ReconciliationApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient);

        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/issues", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new { number = 7, body = "", pull_request = new { url = "https://api.github.com/repos/acme/platform/pulls/7" } } }) });

        var service = BuildService(factory);
        await service.RunOnceAsync(CancellationToken.None);

        Assert.Empty(await LoadPipelines(factory, workspaceId));
    }

    [Fact]
    public async Task RecoversPhaseFromCurrentPrReviewsWhenTheWebhookWasLost()
    {
        using var factory = new ReconciliationApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient);
        await RegisterCredential(setupClient, workspaceId, "revisor", "revisor-bot");
        await RegisterCredential(setupClient, workspaceId, "qa", "qa-bot");
        var pipelineId = await SeedPipelineInstance(factory, workspaceId, externalRef: "7", fase: PipelinePhase.CodeReview, prRef: "42");

        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/issues", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/pulls/42/reviews", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[]
            {
                new { user = new { login = "revisor-bot" }, state = "APPROVED" },
                new { user = new { login = "qa-bot" }, state = "APPROVED" }
            }) });

        var service = BuildService(factory);
        await service.RunOnceAsync(CancellationToken.None);

        var pipelines = await LoadPipelines(factory, workspaceId);
        var pipeline = Assert.Single(pipelines);
        Assert.Equal(PipelinePhase.Seguranca, pipeline.FaseAtual);
        Assert.Equal(GateStatus.Pending, pipeline.GateStatus);
        Assert.Equal(pipelineId, pipeline.Id);
    }

    [Fact]
    public async Task ASupersededChangesRequestedReviewIsNotTreatedAsApproved()
    {
        using var factory = new ReconciliationApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient);
        await RegisterCredential(setupClient, workspaceId, "revisor", "revisor-bot");
        await SeedPipelineInstance(factory, workspaceId, externalRef: "7", fase: PipelinePhase.CodeReview, prRef: "42");

        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/issues", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
        // Same reviewer approved, then later requested changes - only the latest state should count.
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/pulls/42/reviews", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[]
            {
                new { user = new { login = "revisor-bot" }, state = "APPROVED" },
                new { user = new { login = "revisor-bot" }, state = "CHANGES_REQUESTED" }
            }) });

        var service = BuildService(factory);
        await service.RunOnceAsync(CancellationToken.None);

        var pipeline = Assert.Single(await LoadPipelines(factory, workspaceId));
        Assert.Equal(PipelinePhase.CodeReview, pipeline.FaseAtual);
    }

    [Fact]
    public async Task PipelinesAlreadyAtDeployAreSkipped()
    {
        using var factory = new ReconciliationApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient);
        await SeedPipelineInstance(factory, workspaceId, externalRef: "7", fase: PipelinePhase.Deploy, prRef: "42");

        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/issues", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/pulls/42/reviews", _ =>
            throw new InvalidOperationException("must not query reviews for a pipeline_instance already at Deploy"));

        var service = BuildService(factory);
        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PipelinePhase.Deploy, Assert.Single(await LoadPipelines(factory, workspaceId)).FaseAtual);
    }

    [Fact]
    public async Task PlatformFailureIsSkippedWithoutThrowing()
    {
        using var factory = new ReconciliationApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient);
        await SeedPipelineInstance(factory, workspaceId, externalRef: "7");

        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/issues", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var service = BuildService(factory);
        var ex = await Record.ExceptionAsync(() => service.RunOnceAsync(CancellationToken.None));

        Assert.Null(ex);
    }
}
