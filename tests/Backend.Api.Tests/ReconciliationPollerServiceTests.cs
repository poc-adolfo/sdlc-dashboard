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
using Microsoft.EntityFrameworkCore;
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

    private static async Task<long> SeedSpec(ReconciliationApplicationFactory factory, long workspaceId, string path)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var spec = new Spec { WorkspaceId = workspaceId, Path = path, Title = "Título", Status = "rascunho", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Specs.Add(spec);
        await db.SaveChangesAsync();
        return spec.Id;
    }

    private static async Task SeedSpecPublication(ReconciliationApplicationFactory factory, long workspaceId, string externalRef, long? specId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SpecPublications.Add(new SpecPublication { WorkspaceId = workspaceId, SpecId = specId, ExternalRef = externalRef, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
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
    public async Task RecreatesAPipelineInstanceForASpecPublicationThatNeverGotOne()
    {
        using var factory = new ReconciliationApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient);
        var specId = await SeedSpec(factory, workspaceId, "specs/us-exemplo.md");
        await SeedSpecPublication(factory, workspaceId, "7", specId);

        var service = BuildService(factory);
        await service.RunOnceAsync(CancellationToken.None);

        var pipelines = await LoadPipelines(factory, workspaceId);
        var created = Assert.Single(pipelines);
        Assert.Equal("7", created.ExternalRef);
        Assert.Equal(PipelinePhase.Requisitos, created.FaseAtual);
        Assert.Equal(specId, created.SpecId);
    }

    [Fact]
    public async Task DoesNothingWhenNoSpecPublicationExists()
    {
        // Security review on PR #17: with no durable spec_publication record of a "Subir US" publish,
        // there is nothing here to trust - unlike the earlier label/marker design, there is no
        // GitHub-readable input at all left in this path for an outside collaborator to forge.
        using var factory = new ReconciliationApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient);

        var service = BuildService(factory);
        await service.RunOnceAsync(CancellationToken.None);

        Assert.Empty(await LoadPipelines(factory, workspaceId));
    }

    [Fact]
    public async Task DoesNotDuplicateAnAlreadyExistingPipelineInstance()
    {
        using var factory = new ReconciliationApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient);
        await SeedPipelineInstance(factory, workspaceId, externalRef: "7");
        await SeedSpecPublication(factory, workspaceId, "7");

        var service = BuildService(factory);
        await service.RunOnceAsync(CancellationToken.None);

        Assert.Single(await LoadPipelines(factory, workspaceId));
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

        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/pulls/42/reviews", _ =>
            throw new InvalidOperationException("must not query reviews for a pipeline_instance already at Deploy"));

        var service = BuildService(factory);
        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PipelinePhase.Deploy, Assert.Single(await LoadPipelines(factory, workspaceId)).FaseAtual);
    }

    [Fact]
    public async Task PlatformFailureDuringPhaseDriftReconciliationIsSkippedWithoutThrowing()
    {
        using var factory = new ReconciliationApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient);
        await SeedPipelineInstance(factory, workspaceId, externalRef: "7", fase: PipelinePhase.CodeReview, prRef: "42");

        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/pulls/42/reviews", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var service = BuildService(factory);
        var ex = await Record.ExceptionAsync(() => service.RunOnceAsync(CancellationToken.None));

        Assert.Null(ex);
        Assert.Equal(PipelinePhase.CodeReview, Assert.Single(await LoadPipelines(factory, workspaceId)).FaseAtual);
    }
}

public sealed class ReconciliationPollerServiceHostRegistrationTests
{
    // QA finding on PR #17: Program.cs registers ReconciliationPollerService as a hosted service
    // everywhere except the Testing environment (to avoid unwanted background platform calls during
    // the test run - see ReconciliationApplicationFactory above, which stays on "Testing" for that
    // reason). This verifies the *other* half of that conditional actually holds: outside Testing, the
    // hosted service really is registered and starts without throwing. "Staging" here is just "some
    // real environment name that is neither Development nor Testing".
    private sealed class NonTestingApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"reconcile-hosting-tests-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Staging");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Username"] = "operator",
                ["Authentication:Password"] = "secret",
                ["Authentication:SigningKey"] = "DoBqIVy5zTyTGicih2WShaYg6goTsq0lvS7XlPiHWps=",
                ["Authentication:SecureCookie"] = "false",
                ["ConnectionStrings:Default"] = $"Data Source={_databasePath}",
                ["Analista:ApiServerBaseUrl"] = "https://analista.test",
                ["Analista:AllowedHost"] = "analista.test",
                // Staying well clear of any real GitHub/DB traffic: no workspaces get created in this
                // test, so RunOnceAsync's first pass finds nothing to do and just waits on the timer.
                ["Reconciliation:IntervalMinutes"] = "60"
            }));
            builder.ConfigureTestServices(services => services.AddSingleton<ISecretStore>(new FakeSecretStore()));
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

    [Fact]
    public void IsRegisteredAsAHostedServiceOutsideTheTestingEnvironment()
    {
        using var factory = new NonTestingApplicationFactory();

        var hosted = factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>();

        Assert.Contains(hosted, s => s is ReconciliationPollerService);
    }
}

public sealed class TryCreatePipelineInstanceAsyncTests
{
    private static AppDbContext CreateMigratedContext(out Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        var db = new AppDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
        return db;
    }

    [Fact]
    public async Task ConfirmedUniqueIndexRaceIsSwallowed()
    {
        using var db = CreateMigratedContext(out var connection);
        using var _ = connection;
        var workspace = new Backend.Persistence.Domain.Workspace { Name = "W", Slug = "w", PlatformRef = "acme/platform" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        // Simulates the losing side of a real race: something else already created this row.
        db.PipelineInstances.Add(new PipelineInstance { WorkspaceId = workspace.Id, ExternalRef = "7", FaseAtual = PipelinePhase.Requisitos, GateStatus = GateStatus.Approved, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var ex = await Record.ExceptionAsync(() => ReconciliationPollerService.TryCreatePipelineInstanceAsync(db, workspace.Id, "7", CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task GenuinePersistenceFailureIsNotSwallowedAsTheBenignUniqueIndexRace()
    {
        // Revisor finding on PR #16: catching DbUpdateException unconditionally treated any
        // persistence failure as the expected (WorkspaceId, ExternalRef) race. Here the workspace
        // never actually exists in the DB (a phantom id), so the insert fails on the FK constraint,
        // not the unique index - this must propagate, not be swallowed as if a concurrent pass had
        // already recreated the row.
        using var db = CreateMigratedContext(out var connection);
        using var _ = connection;

        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() =>
            ReconciliationPollerService.TryCreatePipelineInstanceAsync(db, workspaceId: 999999, "7", CancellationToken.None));
    }

    [Fact]
    public async Task TwoGenuinelyConcurrentAttemptsForTheSameExternalRefLeaveExactlyOneRow()
    {
        // QA finding on PR #17: ConfirmedUniqueIndexRaceIsSwallowed only pre-creates the "losing" row
        // and calls TryCreatePipelineInstanceAsync once in the same DbContext - it never exercises a
        // real interleaving. This drives two independent connections/DbContexts against the same
        // on-disk SQLite file (an in-memory ":memory:" connection can't be shared across connections),
        // mirroring two actual reconciliation passes racing each other.
        var dbPath = Path.Combine(Path.GetTempPath(), $"reconcile-race-{Guid.NewGuid():N}.db");
        try
        {
            long workspaceId;
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var db = new AppDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
                db.Database.Migrate();
                var workspace = new Backend.Persistence.Domain.Workspace { Name = "W", Slug = "w", PlatformRef = "acme/platform" };
                db.Workspaces.Add(workspace);
                await db.SaveChangesAsync();
                workspaceId = workspace.Id;
            }

            async Task Attempt()
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                connection.Open();
                using var db = new AppDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
                await ReconciliationPollerService.TryCreatePipelineInstanceAsync(db, workspaceId, "7", CancellationToken.None);
            }

            var attempt1 = Attempt();
            var attempt2 = Attempt();
            // Neither call may throw: whichever side loses the race must observe the row the winner
            // just committed and swallow its own DbUpdateException.
            await Task.WhenAll(attempt1, attempt2);

            using var verifyConnection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            verifyConnection.Open();
            using var verifyDb = new AppDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AppDbContext>().UseSqlite(verifyConnection).Options);
            Assert.Equal(1, await verifyDb.PipelineInstances.CountAsync(p => p.WorkspaceId == workspaceId && p.ExternalRef == "7"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-shm", "-wal" })
            {
                var path = dbPath + suffix;
                try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
            }
        }
    }
}
