using System.Text.Json.Serialization;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Apis;

/// <summary>
/// Best-effort report from the skill <c>implementar-spec</c> (seção 6.1/6.6) for the two phase
/// transitions the application otherwise never observes: Design (Arquiteto's DoR gate approved) and
/// Dev (Dev dispatch triggered) - the operator drives both outside this application. Not a retry-able
/// contract: if this call fails, nothing is retried here or by the skill - the next real platform event
/// (<c>pull_request.opened</c>) corrects <c>fase_atual</c> anyway (seção 6.1), so there is nothing
/// critical to lose, only display granularity.
/// </summary>
public static class PhaseTransitionEndpoints
{
    public static IEndpointRouteBuilder MapPhaseTransitionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/pipeline-instances/{id:long}/phase-transitions", Report);
        return app;
    }

    private static async Task<IResult> Report(long id, PhaseTransitionReportRequest? request, AppDbContext db, CancellationToken ct)
    {
        var errors = Validate(request, out var fase);
        if (errors.Count > 0) return Results.UnprocessableEntity(new { errors });

        var pipeline = await db.PipelineInstances.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (pipeline is null) return Results.NotFound();

        // deliveryId: null - there is no platform delivery to dedup against for a reported transition
        // (same reuse pattern as ReconciliationPollerService). AdvancePhaseAsync's forward-only guard is
        // what makes a repeated identical report a no-op instead of a duplicate phase_transition row.
        await WebhookEndpoints.AdvancePhaseAsync(db, pipeline, fase!.Value, request!.SourceEvent!, deliveryId: null, ct);
        return Results.StatusCode(StatusCodes.Status202Accepted);
    }

    private static List<string> Validate(PhaseTransitionReportRequest? request, out PipelinePhase? fase)
    {
        fase = null;
        var errors = new List<string>();
        if (request is null) { errors.Add("request: is required"); return errors; }

        // Only Design/Dev are ever "reported" (seção 6.1) - every other phase is inferred automatically
        // from a webhook (WebhookEndpoints) or reconciliation (ReconciliationPollerService). Accepting
        // arbitrary values here would let a caller skip that inference and set any phase directly.
        if (string.Equals(request.Fase, "design", StringComparison.OrdinalIgnoreCase)) fase = PipelinePhase.Design;
        else if (string.Equals(request.Fase, "dev", StringComparison.OrdinalIgnoreCase)) fase = PipelinePhase.Dev;
        else errors.Add("fase: must be one of design, dev");

        if (string.IsNullOrWhiteSpace(request.SourceEvent)) errors.Add("source_event: is required");

        return errors;
    }
}

public sealed record PhaseTransitionReportRequest(
    [property: JsonPropertyName("fase")] string? Fase,
    [property: JsonPropertyName("source_event")] string? SourceEvent);
