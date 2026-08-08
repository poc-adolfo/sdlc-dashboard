using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Backend.Api.Apis;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Tests;

public sealed class WebhookEndpointsTests
{
    private const string WebhookSecret = "webhook-shared-secret";

    private static async Task<HttpClient> AuthenticatedClient(CredentialApplicationFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<long> CreateGitHubWorkspace(HttpClient client, CredentialApplicationFactory factory, string appSecretRef = "webhook-secret-ref")
    {
        factory.Secrets.Seeded[appSecretRef] = WebhookSecret;
        var response = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform", app_secret_ref = appSecretRef });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private static async Task<long> SeedPipelineInstance(CredentialApplicationFactory factory, long workspaceId, string externalRef, PipelinePhase fase = PipelinePhase.Requisitos, string? prRef = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pipeline = new PipelineInstance { WorkspaceId = workspaceId, ExternalRef = externalRef, FaseAtual = fase, PrRef = prRef, GateStatus = GateStatus.Approved, CreatedAt = DateTime.UtcNow };
        db.PipelineInstances.Add(pipeline);
        await db.SaveChangesAsync();
        return pipeline.Id;
    }

    private static async Task RegisterCredential(HttpClient client, long workspaceId, string perfil, string platformUsername)
    {
        var response = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/credenciais", new { perfil, platform_username = platformUsername, token = "irrelevant-for-these-tests" });
        response.EnsureSuccessStatusCode();
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

    private static HttpRequestMessage SignedGitHubRequest(long workspaceId, string eventType, string deliveryId, object payload)
    {
        var body = JsonSerializer.Serialize(payload);
        var signature = "sha256=" + WebhookEndpoints.ComputeGitHubSignatureHex(WebhookSecret, body);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{workspaceId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Hub-Signature-256", signature);
        request.Headers.Add("X-GitHub-Delivery", deliveryId);
        request.Headers.Add("X-GitHub-Event", eventType);
        return request;
    }

    private static object PullRequestOpenedPayload(long number, string bodyText, string action = "opened") => new
    {
        action,
        pull_request = new { number, body = bodyText }
    };

    private static object PullRequestReviewApprovedPayload(long prNumber, string reviewerLogin) => new
    {
        action = "submitted",
        review = new { state = "approved", user = new { login = reviewerLogin } },
        pull_request = new { number = prNumber }
    };

    [Fact]
    public async Task DoesNotRequireASessionCookie()
    {
        using var factory = new CredentialApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient, factory);

        using var anonymousClient = factory.CreateClient();
        using var request = SignedGitHubRequest(workspaceId, "pull_request", "delivery-1", PullRequestOpenedPayload(1, "no closes reference here"));
        var response = await anonymousClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task MissingOrInvalidSignatureIsRejected()
    {
        using var factory = new CredentialApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient, factory);
        using var client = factory.CreateClient();

        var unsigned = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{workspaceId}") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(unsigned)).StatusCode);

        var wrongSignature = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{workspaceId}") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        wrongSignature.Headers.Add("X-Hub-Signature-256", "sha256=" + new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrongSignature)).StatusCode);
    }

    // No automated test for the 413-on-oversized-body path: enforcement of
    // IHttpMaxRequestBodySizeFeature.MaxRequestBodySize happens inside Kestrel's real request-body
    // stream (Http1MessageBody), which WebApplicationFactory's in-memory TestServer never exercises -
    // TestServer accepts a fully-buffered body regardless of the configured limit, so a test asserting
    // 413 here would only pass if the feature value were read back, not if Kestrel actually enforced
    // it. Setting IHttpMaxRequestBodySizeFeature.MaxRequestBodySize before reading is the documented,
    // standard ASP.NET Core mechanism for this (see Receive's comment); Kestrel honoring it is a
    // framework guarantee, not app logic that needs re-verifying here.

    [Fact]
    public async Task MissingWorkspaceReturnsNotFound()
    {
        using var factory = new CredentialApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/webhooks/999999", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PullRequestOpenedCorrelatesByClosesReferenceAndAdvancesToCodeReview()
    {
        using var factory = new CredentialApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient, factory);
        var pipelineId = await SeedPipelineInstance(factory, workspaceId, externalRef: "7");
        using var client = factory.CreateClient();

        using var request = SignedGitHubRequest(workspaceId, "pull_request", "delivery-1", PullRequestOpenedPayload(42, "Implements the feature.\n\nCloses #7"));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var pipeline = await LoadPipeline(factory, pipelineId);
        Assert.Equal(PipelinePhase.CodeReview, pipeline.FaseAtual);
        Assert.Equal("42", pipeline.PrRef);
        Assert.Equal(GateStatus.Pending, pipeline.GateStatus);
        Assert.Equal(1, await PhaseTransitionCount(factory, pipelineId));
    }

    [Theory]
    [InlineData("Closes #7")]
    [InlineData("closes #7")]
    [InlineData("Fixes #7")]
    [InlineData("resolved #7")]
    public async Task RecognizesAllGitHubClosingKeywords(string closingPhrase)
    {
        using var factory = new CredentialApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient, factory);
        var pipelineId = await SeedPipelineInstance(factory, workspaceId, externalRef: "7");
        using var client = factory.CreateClient();

        using var request = SignedGitHubRequest(workspaceId, "pull_request", "delivery-1", PullRequestOpenedPayload(42, $"Body. {closingPhrase}"));
        await client.SendAsync(request);

        Assert.Equal(PipelinePhase.CodeReview, (await LoadPipeline(factory, pipelineId)).FaseAtual);
    }

    [Fact]
    public async Task DuplicateDeliveryIdDoesNotDuplicateThePhaseTransition()
    {
        using var factory = new CredentialApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient, factory);
        var pipelineId = await SeedPipelineInstance(factory, workspaceId, externalRef: "7");
        using var client = factory.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            using var request = SignedGitHubRequest(workspaceId, "pull_request", "delivery-1", PullRequestOpenedPayload(42, "Closes #7"));
            Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(request)).StatusCode);
        }

        Assert.Equal(1, await PhaseTransitionCount(factory, pipelineId));
    }

    [Fact]
    public async Task ReviewApprovalAdvancesPhaseAccordingToApproverPerfil()
    {
        using var factory = new CredentialApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient, factory);
        await RegisterCredential(setupClient, workspaceId, "revisor", "revisor-bot");
        await RegisterCredential(setupClient, workspaceId, "qa", "qa-bot");
        await RegisterCredential(setupClient, workspaceId, "seguranca", "security-bot");
        var pipelineId = await SeedPipelineInstance(factory, workspaceId, externalRef: "7", fase: PipelinePhase.CodeReview, prRef: "42");
        using var client = factory.CreateClient();

        using var revisorApproval = SignedGitHubRequest(workspaceId, "pull_request_review", "delivery-revisor", PullRequestReviewApprovedPayload(42, "revisor-bot"));
        await client.SendAsync(revisorApproval);
        Assert.Equal(PipelinePhase.Qa, (await LoadPipeline(factory, pipelineId)).FaseAtual);
        Assert.Equal(GateStatus.Pending, (await LoadPipeline(factory, pipelineId)).GateStatus);

        using var qaApproval = SignedGitHubRequest(workspaceId, "pull_request_review", "delivery-qa", PullRequestReviewApprovedPayload(42, "qa-bot"));
        await client.SendAsync(qaApproval);
        Assert.Equal(PipelinePhase.Seguranca, (await LoadPipeline(factory, pipelineId)).FaseAtual);

        using var securityApproval = SignedGitHubRequest(workspaceId, "pull_request_review", "delivery-seguranca", PullRequestReviewApprovedPayload(42, "security-bot"));
        await client.SendAsync(securityApproval);
        var final = await LoadPipeline(factory, pipelineId);
        Assert.Equal(PipelinePhase.Deploy, final.FaseAtual);
        Assert.Equal(GateStatus.Approved, final.GateStatus); // seção 6.5: last real approval, no next platform gate

        Assert.Equal(3, await PhaseTransitionCount(factory, pipelineId));
    }

    [Fact]
    public async Task ApprovalByUnregisteredReviewerDoesNotAdvancePhase()
    {
        using var factory = new CredentialApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient, factory);
        var pipelineId = await SeedPipelineInstance(factory, workspaceId, externalRef: "7", fase: PipelinePhase.CodeReview, prRef: "42");
        using var client = factory.CreateClient();

        using var request = SignedGitHubRequest(workspaceId, "pull_request_review", "delivery-1", PullRequestReviewApprovedPayload(42, "someone-not-registered"));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(PipelinePhase.CodeReview, (await LoadPipeline(factory, pipelineId)).FaseAtual);
        Assert.Equal(0, await PhaseTransitionCount(factory, pipelineId));
    }

    [Fact]
    public async Task OutOfOrderEventNeverRegressesThePhase()
    {
        using var factory = new CredentialApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient, factory);
        await RegisterCredential(setupClient, workspaceId, "revisor", "revisor-bot");
        // Already past CodeReview (e.g. QA already approved through a previous, faster-processed event).
        var pipelineId = await SeedPipelineInstance(factory, workspaceId, externalRef: "7", fase: PipelinePhase.Qa, prRef: "42");
        using var client = factory.CreateClient();

        using var staleRevisorApproval = SignedGitHubRequest(workspaceId, "pull_request_review", "delivery-late", PullRequestReviewApprovedPayload(42, "revisor-bot"));
        await client.SendAsync(staleRevisorApproval);

        Assert.Equal(PipelinePhase.Qa, (await LoadPipeline(factory, pipelineId)).FaseAtual);
        Assert.Equal(0, await PhaseTransitionCount(factory, pipelineId));
    }

    [Fact]
    public async Task MalformedJsonBodyIsAckedWithoutError()
    {
        using var factory = new CredentialApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient, factory);
        using var client = factory.CreateClient();

        const string body = "{not-json";
        var signature = "sha256=" + WebhookEndpoints.ComputeGitHubSignatureHex(WebhookSecret, body);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{workspaceId}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Hub-Signature-256", signature);
        request.Headers.Add("X-GitHub-Event", "pull_request");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task PullRequestOpenedWithoutAClosesReferenceIsAckedWithoutCorrelating()
    {
        using var factory = new CredentialApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(setupClient, factory);
        var pipelineId = await SeedPipelineInstance(factory, workspaceId, externalRef: "7");
        using var client = factory.CreateClient();

        using var request = SignedGitHubRequest(workspaceId, "pull_request", "delivery-1", PullRequestOpenedPayload(42, "no reference to any issue"));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var pipeline = await LoadPipeline(factory, pipelineId);
        Assert.Equal(PipelinePhase.Requisitos, pipeline.FaseAtual);
        Assert.Null(pipeline.PrRef);
    }

    [Fact]
    public async Task AzureDevOpsBasicAuthIsVerifiedAndAcksWithoutAPhaseTransition()
    {
        using var factory = new CredentialApplicationFactory();
        using var setupClient = await AuthenticatedClient(factory);
        factory.Secrets.Seeded["ado-webhook-secret-ref"] = WebhookSecret;
        var create = await setupClient.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "azure_devops", platform_ref = "org/project", app_secret_ref = "ado-webhook-secret-ref" });
        create.EnsureSuccessStatusCode();
        var workspaceId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var pipelineId = await SeedPipelineInstance(factory, workspaceId, externalRef: "7");
        using var client = factory.CreateClient();

        using var unauthorized = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{workspaceId}") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(unauthorized)).StatusCode);

        using var authorized = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{workspaceId}") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        authorized.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + WebhookSecret)));
        var response = await client.SendAsync(authorized);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(0, await PhaseTransitionCount(factory, pipelineId));
    }
}

public sealed class WebhookParsingTests
{
    [Theory]
    [InlineData("Closes #42", "42")]
    [InlineData("closes #42", "42")]
    [InlineData("Closed #42", "42")]
    [InlineData("Fix #42", "42")]
    [InlineData("Fixes #42", "42")]
    [InlineData("Fixed #42", "42")]
    [InlineData("Resolve #42", "42")]
    [InlineData("Resolves #42", "42")]
    [InlineData("Resolved #42", "42")]
    [InlineData("no keyword here #42", null)]
    [InlineData("", null)]
    public void ExtractClosesReferenceRecognizesGitHubKeywordVariants(string body, string? expected)
    {
        Assert.Equal(expected, WebhookEndpoints.ExtractClosesReference(body));
    }

    [Theory]
    [InlineData(Perfil.Revisor, PipelinePhase.Qa)]
    [InlineData(Perfil.Qa, PipelinePhase.Seguranca)]
    [InlineData(Perfil.Seguranca, PipelinePhase.Deploy)]
    [InlineData(Perfil.Dev, null)]
    [InlineData(Perfil.AnalistaRequisitos, null)]
    [InlineData(Perfil.Arquiteto, null)]
    [InlineData(Perfil.ReleaseDeploy, null)]
    public void TargetPhaseForApproverMapsOnlyReviewGatePerfis(Perfil perfil, PipelinePhase? expected)
    {
        Assert.Equal(expected, WebhookEndpoints.TargetPhaseForApprover(perfil));
    }

    [Fact]
    public void ComputeGitHubSignatureHexIsDeterministicAndCaseInsensitiveHex()
    {
        var a = WebhookEndpoints.ComputeGitHubSignatureHex("secret", "body");
        var b = WebhookEndpoints.ComputeGitHubSignatureHex("secret", "body");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.Equal(a, a.ToLowerInvariant());
    }
}

public sealed class AdvancePhaseAsyncTests
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
    public async Task GenuinePersistenceFailureIsNotSwallowedAsTheBenignDedupRace()
    {
        // Revisor finding on PR #15: catching DbUpdateException unconditionally treated any persistence
        // failure as the expected duplicate-delivery race. Here the PipelineInstance never actually
        // exists in the DB (a phantom Id), so the PhaseTransition insert fails on the FK constraint, not
        // the delivery-id unique index - this must propagate, not be swallowed as if someone else had
        // already recorded the same delivery.
        using var db = CreateMigratedContext(out var connection);
        using var _ = connection;
        var phantomPipeline = new PipelineInstance { Id = 999999, WorkspaceId = 1, ExternalRef = "x", FaseAtual = PipelinePhase.Requisitos, GateStatus = GateStatus.Approved, CreatedAt = DateTime.UtcNow };

        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() =>
            WebhookEndpoints.AdvancePhaseAsync(db, phantomPipeline, PipelinePhase.CodeReview, "test", deliveryId: "delivery-x", CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmedDuplicateDeliveryIsSwallowed()
    {
        using var db = CreateMigratedContext(out var connection);
        using var _ = connection;
        var workspace = new Backend.Persistence.Domain.Workspace { Name = "W", Slug = "w", PlatformRef = "acme/platform" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var pipeline = new PipelineInstance { WorkspaceId = workspace.Id, ExternalRef = "1", FaseAtual = PipelinePhase.Requisitos, GateStatus = GateStatus.Approved, CreatedAt = DateTime.UtcNow };
        db.PipelineInstances.Add(pipeline);
        await db.SaveChangesAsync();

        // Simulates the losing side of a real race: a transition for this exact delivery already exists.
        db.PhaseTransitions.Add(new PhaseTransition { PipelineInstanceId = pipeline.Id, Fase = PipelinePhase.CodeReview, EnteredAt = DateTime.UtcNow, SourceEvent = "pull_request.opened", DeliveryId = "delivery-1" });
        await db.SaveChangesAsync();

        var ex = await Record.ExceptionAsync(() =>
            WebhookEndpoints.AdvancePhaseAsync(db, pipeline, PipelinePhase.CodeReview, "pull_request.opened", deliveryId: "delivery-1", CancellationToken.None, setPrRef: "1"));

        Assert.Null(ex);
    }
}
