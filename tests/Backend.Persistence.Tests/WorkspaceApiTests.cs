using System.Net;
using System.Net.Http.Json;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Backend.Persistence.Tests;

public sealed class WorkspaceApiTests : IClassFixture<WorkspaceApiFactory>
{
    private readonly WorkspaceApiFactory factory;
    public WorkspaceApiTests(WorkspaceApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task CreateWithExistingClientAndOptionalFieldsReturnsAndPersistsContract()
    {
        long clientId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var client = new Client { Name = "Acme" };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            clientId = client.Id;
        }

        using var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync("/workspaces", new
        {
            name = "Acme App",
            platform = "github",
            platform_ref = "acme/app",
            client_id = clientId,
            specs_path = "docs/specs",
            specs_repo = "acme/specs",
            code_repo = "acme/app",
            ado_work_item_type = "Issue",
            app_secret_ref = "secret/acme"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkspaceResponse>();
        Assert.NotNull(body);
        Assert.Equal("Acme App", body!.Name);
        Assert.Equal("acme-app", body.Slug);
        Assert.Equal("github", body.Platform);
        Assert.Equal("acme/app", body.PlatformRef);
        Assert.Equal(clientId, body.ClientId);
        Assert.Equal("active", body.Status);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var saved = await verifyScope.ServiceProvider.GetRequiredService<AppDbContext>().Workspaces
            .AsNoTracking().SingleAsync(x => x.Id == body.Id);
        Assert.Equal("docs/specs", saved.SpecsPath);
        Assert.Equal("acme/specs", saved.SpecsRepo);
        Assert.Equal("acme/app", saved.CodeRepo);
        Assert.Equal("Issue", saved.AdoWorkItemType);
        Assert.Equal("secret/acme", saved.AppSecretRef);
    }

    [Fact]
    public async Task WorkspaceMutationsRequireValidSessionCookie()
    {
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Remove("Cookie");
        var requests = new Func<Task<HttpResponseMessage>>[]
        {
            () => http.PostAsJsonAsync("/workspaces", new { name = "Unauthenticated", platform = "github", platform_ref = "unauthenticated" }),
            () => http.PatchAsJsonAsync("/workspaces/1", new { name = "Unauthenticated" }),
            () => http.PostAsync("/workspaces/1/archive", null)
        };
        foreach (var request in requests)
            Assert.Equal(HttpStatusCode.Unauthorized, (await request()).StatusCode);

        http.DefaultRequestHeaders.Add("Cookie", "sdlc_session=invalid-or-tampered");
        foreach (var request in requests)
            Assert.Equal(HttpStatusCode.Unauthorized, (await request()).StatusCode);
    }

    [Fact]
    public async Task CreateWithMissingClientReturns422()
    {
        using var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync("/workspaces", new
        { name = "Missing Client", platform = "github", platform_ref = "acme/missing", client_id = long.MaxValue });
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
    }

    [Fact]
    public async Task DuplicateSlugIsRejectedOnCreateAndUpdate()
    {
        using var http = factory.CreateClient();
        var first = await http.PostAsJsonAsync("/workspaces", new { name = "Duplicate One", platform = "github", platform_ref = "one" });
        var second = await http.PostAsJsonAsync("/workspaces", new { name = "Other", platform = "github", platform_ref = "two" });
        var firstBody = await first.Content.ReadFromJsonAsync<WorkspaceResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<WorkspaceResponse>();

        var createDuplicate = await http.PostAsJsonAsync("/workspaces", new { name = "Duplicate One", platform = "github", platform_ref = "three" });
        Assert.Equal((HttpStatusCode)422, createDuplicate.StatusCode);
        var updateDuplicate = await http.PatchAsJsonAsync($"/workspaces/{secondBody!.Id}", new { name = "Duplicate One" });
        Assert.Equal((HttpStatusCode)422, updateDuplicate.StatusCode);
        Assert.Equal("duplicate-one", firstBody!.Slug);
    }

    [Fact]
    public async Task PatchUpdatesAllFieldsAndReturnsPersistedValues()
    {
        using var http = factory.CreateClient();
        var created = await http.PostAsJsonAsync("/workspaces", new { name = "Before", platform = "github", platform_ref = "before" });
        var initial = await created.Content.ReadFromJsonAsync<WorkspaceResponse>();
        var response = await http.PatchAsJsonAsync($"/workspaces/{initial!.Id}", new
        {
            name = "After Name", platform = "azure_devops", platform_ref = "org/project", specs_path = "new/specs",
            specs_repo = "org/specs", code_repo = "org/code", ado_work_item_type = "Bug", app_secret_ref = "new-secret"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkspaceResponse>();
        Assert.Equal("After Name", body!.Name);
        Assert.Equal("after-name", body.Slug);
        Assert.Equal("azure_devops", body.Platform);
        Assert.Equal("org/project", body.PlatformRef);

        await using var scope = factory.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Workspaces.AsNoTracking().SingleAsync(x => x.Id == initial.Id);
        Assert.Equal("new/specs", saved.SpecsPath);
        Assert.Equal("org/specs", saved.SpecsRepo);
        Assert.Equal("org/code", saved.CodeRepo);
        Assert.Equal("Bug", saved.AdoWorkItemType);
        Assert.Equal("new-secret", saved.AppSecretRef);
    }

    [Fact]
    public async Task PatchRejectsMissingWorkspaceAndInvalidPayloads()
    {
        using var http = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.PatchAsJsonAsync("/workspaces/999999", new { name = "x" })).StatusCode);
        var created = await http.PostAsJsonAsync("/workspaces", new { name = "Validation", platform = "github", platform_ref = "valid" });
        var workspace = await created.Content.ReadFromJsonAsync<WorkspaceResponse>();
        Assert.Equal((HttpStatusCode)422, (await http.PatchAsJsonAsync($"/workspaces/{workspace!.Id}", new { platform = "invalid" })).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await http.PatchAsJsonAsync($"/workspaces/{workspace.Id}", new { platform_ref = " " })).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await http.PatchAsJsonAsync($"/workspaces/{workspace.Id}", new { name = " " })).StatusCode);
    }

    [Fact]
    public async Task ArchiveMissingWorkspaceIs404AndArchivePersists()
    {
        using var http = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/workspaces/999999/archive", null)).StatusCode);
        var created = await http.PostAsJsonAsync("/workspaces", new { name = "To Archive", platform = "github", platform_ref = "archive" });
        var workspace = await created.Content.ReadFromJsonAsync<WorkspaceResponse>();
        var archived = await http.PostAsync($"/workspaces/{workspace!.Id}/archive", null);
        var body = await archived.Content.ReadFromJsonAsync<WorkspaceResponse>();
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal("archived", body!.Status);
        await using var scope = factory.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Workspaces.AsNoTracking().SingleAsync(x => x.Id == workspace.Id);
        Assert.Equal(WorkspaceStatus.Archived, saved.Status);
    }

    [Fact]
    public async Task PlatformRefOnlyChangeIsConflictWhenPipelineExists()
    {
        long id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var workspace = new Workspace { Name = "Locked-" + Guid.NewGuid().ToString("N"), Slug = "locked-unique", Platform = WorkspacePlatform.Github, PlatformRef = "locked" };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();
            db.PipelineInstances.Add(new PipelineInstance { WorkspaceId = workspace.Id, ExternalRef = "pipeline" });
            await db.SaveChangesAsync();
            id = workspace.Id;
        }
        using var http = factory.CreateClient();
        var response = await http.PatchAsJsonAsync($"/workspaces/{id}", new { platform_ref = "changed-only" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task InvalidCreateAndMissingClientUpdateReturn422()
    {
        using var http = factory.CreateClient();
        Assert.Equal((HttpStatusCode)422, (await http.PostAsJsonAsync("/workspaces", new { name = "", platform = "other", platform_ref = "" })).StatusCode);
        var created = await http.PostAsJsonAsync("/workspaces", new { name = "Client Validation", platform = "github", platform_ref = "client-validation" });
        var workspace = await created.Content.ReadFromJsonAsync<WorkspaceResponse>();
        Assert.Equal((HttpStatusCode)422, (await http.PatchAsJsonAsync($"/workspaces/{workspace!.Id}", new { client_id = long.MaxValue })).StatusCode);
    }
}

public sealed class WorkspaceApiFactory : WebApplicationFactory<Program>
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"workspace-api-{Guid.NewGuid():N}.db");
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", "Data Source=" + path);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Username"] = "operator", ["Authentication:Password"] = "secret",
            ["Authentication:SigningKey"] = "DoBqIVy5zTyTGicih2WShaYg6goTsq0lvS7XlPiHWps=", ["Authentication:SecureCookie"] = "false"
        }));
    }
    protected override void ConfigureClient(HttpClient client)
    {
        var sessions = Services.GetRequiredService<Backend.Api.Auth.SessionService>();
        client.DefaultRequestHeaders.Add("Cookie", "sdlc_session=" + sessions.Create("operator", DateTimeOffset.UtcNow));
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (File.Exists(path)) File.Delete(path); }
}

public sealed record WorkspaceResponse(long Id, string Name, string Slug, string Platform, string PlatformRef, long? ClientId, string Status, DateTime CreatedAt);