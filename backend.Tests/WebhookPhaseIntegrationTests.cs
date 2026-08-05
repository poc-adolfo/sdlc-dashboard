using System;
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
    public async Task Correct_tenant_is_allowed_and_wrong_tenant_is_rejected()
    {
        var externalRef = $"integration-{Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DashboardDb>();
            var workspace = new Workspace { TenantId = "tenant-correct", Name = "integration", Slug = externalRef };
            db.Workspaces.Add(workspace);
            db.Pipelines.Add(new PipelineInstance { WorkspaceId = workspace.Id, ExternalRef = externalRef });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var payload = new PhaseEvent(externalRef, "Design", "integration.test", GateStatus.Pending);
        client.DefaultRequestHeaders.Add("X-API-Key", "integration-key");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant-wrong");
        var wrong = await client.PostAsJsonAsync("/api/webhooks/phase", payload);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

        client.DefaultRequestHeaders.Remove("X-Tenant-Id");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant-correct");
        var correct = await client.PostAsJsonAsync("/api/webhooks/phase", payload);
        Assert.Equal(HttpStatusCode.OK, correct.StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<DashboardDb>();
        Assert.Equal("Design", await verifyDb.Pipelines.Where(x => x.ExternalRef == externalRef).Select(x => x.CurrentPhase).SingleAsync());
    }
}

public sealed class WebhookPhaseFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:ApiKey"] = "integration-key"
        }));
    }
}
