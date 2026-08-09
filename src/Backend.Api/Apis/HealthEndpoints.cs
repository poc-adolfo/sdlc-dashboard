using Backend.Persistence.Data;

namespace Backend.Api.Apis;

/// <summary>
/// Unauthenticated liveness/readiness probe for the k3s Deployment (item 17 do WBS) - every other route
/// in this app requires a session cookie (SessionMiddleware), so there was previously no endpoint a
/// probe could hit that would ever return 2xx without one. Checks DB connectivity (not just "the
/// process is up") so kubelet doesn't route traffic to a pod whose SQLite PVC failed to mount.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", async (AppDbContext db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        return app;
    }
}
