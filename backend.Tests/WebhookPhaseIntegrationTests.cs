using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SDLC.Dashboard;
using Xunit;

namespace SDLC.Dashboard.Tests;

public sealed class WebhookPhaseIntegrationTests : IClassFixture<WebhookPhaseFactory>
{
    private readonly WebhookPhaseFactory factory;
    public WebhookPhaseIntegrationTests(WebhookPhaseFactory factory) => this.factory = factory;

    [Fact]
    public async Task Webhook_rejects_missing_or_wrong_api_key_and_missing_tenant()
    {
        using var client = factory.CreateClient(); var eventData = new PhaseEvent("does-not-matter", "Design", "test", GateStatus.Pending);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/webhooks/phase", eventData)).StatusCode);
        client.DefaultRequestHeaders.Add("X-API-Key", "wrong"); Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/webhooks/phase", eventData)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-API-Key"); client.DefaultRequestHeaders.Add("X-API-Key", "integration-key");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/webhooks/phase", eventData)).StatusCode);
    }

    [Fact]
    public async Task Webhook_returns_not_found_for_unknown_external_ref()
    {
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-API-Key", "integration-key"); client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant-correct");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/webhooks/phase", new PhaseEvent("missing", "Design", "test", GateStatus.Pending))).StatusCode);
    }

    [Fact]
    public async Task Repeating_the_same_phase_event_is_idempotent()
    {
        var externalRef = $"idempotent-{System.Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope()) { var db = scope.ServiceProvider.GetRequiredService<DashboardDb>(); var w = new Workspace { TenantId = "tenant-idempotent", Name = "integration", Slug = externalRef }; db.Workspaces.Add(w); db.Pipelines.Add(new PipelineInstance { WorkspaceId = w.Id, ExternalRef = externalRef }); await db.SaveChangesAsync(); }
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-API-Key", "integration-key"); client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant-idempotent"); var payload = new PhaseEvent(externalRef, "Design", "integration.test", GateStatus.Pending);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/webhooks/phase", payload)).StatusCode); Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/webhooks/phase", payload)).StatusCode);
        await using var verify = factory.Services.CreateAsyncScope(); var verifyDb = verify.ServiceProvider.GetRequiredService<DashboardDb>(); Assert.Equal(1, await verifyDb.Transitions.CountAsync(x => x.PipelineInstanceId == verifyDb.Pipelines.Single(x => x.ExternalRef == externalRef).Id));
    }
}

public sealed class WebhookPhaseFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) { builder.UseEnvironment("Development"); builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Security:ApiKey"] = "integration-key" })); }
}
