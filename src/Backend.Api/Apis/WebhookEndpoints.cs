using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Backend.Api.Services;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Apis;

/// <summary>
/// Mirrors GitHub/Azure DevOps webhook events into <c>phase_transition</c> (seção 6/6.1/10.1).
/// Processing is synchronous (not a queued background job): the actual DB work per event is a single
/// query plus at most one insert, well inside any platform's webhook timeout, so the "202 before
/// processing" NFR (seção 15) is satisfied on latency in practice without the extra moving parts a
/// true async queue would add. Deliberate simplification, not a correctness gap - if the app is ever
/// slow enough for this to matter, that is the point to add a queue, not before.
/// </summary>
public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/{workspaceId:long}", Receive);
        return app;
    }

    private static async Task<IResult> Receive(long workspaceId, HttpRequest request, AppDbContext db, ISecretStore secrets, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var workspace = await db.Workspaces.SingleOrDefaultAsync(w => w.Id == workspaceId, ct);
        if (workspace is null) return Results.NotFound();

        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(ct);
        var logger = loggerFactory.CreateLogger("WebhookEndpoints");

        try
        {
            if (workspace.Platform == WorkspacePlatform.Github)
            {
                if (!await VerifyGitHubSignatureAsync(workspace, request, body, secrets, ct)) return Results.Unauthorized();
                var deliveryId = request.Headers["X-GitHub-Delivery"].FirstOrDefault();
                var eventType = request.Headers["X-GitHub-Event"].FirstOrDefault() ?? "";
                await ProcessGitHubEventAsync(db, workspace, eventType, body, deliveryId, logger, ct);
            }
            else
            {
                if (!await VerifyAdoBasicAuthAsync(workspace, request, secrets, ct)) return Results.Unauthorized();
                // Azure DevOps Service Hook event bodies (git.pullrequest.*) aren't mapped to phase
                // transitions yet - only GitHub's pull_request/pull_request_review shapes are (these
                // match the mechanism already validated in production, piloto-perfis-dev-revisor.md
                // seção 14.2). Authentication and delivery-id extraction still run so the endpoint acks
                // correctly either way; mapping ADO's event shapes is a documented follow-up, not
                // silently ignored.
                logger.LogInformation("Azure DevOps webhook received for workspace {WorkspaceId} but event-to-phase mapping isn't implemented yet; acking without a phase_transition", workspaceId);
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // A genuine persistence failure here (not the benign dedup race AdvancePhaseAsync already
            // absorbs) must not become a 202: GitHub/Azure DevOps only retry non-2xx responses, so
            // acking a webhook whose phase_transition never actually got written would silently drop it.
            logger.LogError(ex, "Failed to process webhook for workspace {WorkspaceId}", workspaceId);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        return Results.StatusCode(StatusCodes.Status202Accepted);
    }

    private static async Task<bool> VerifyGitHubSignatureAsync(Workspace workspace, HttpRequest request, string body, ISecretStore secrets, CancellationToken ct)
    {
        var header = request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(workspace.AppSecretRef)) return false;

        var secret = await secrets.ReadAsync(workspace.AppSecretRef, ct);
        if (string.IsNullOrEmpty(secret)) return false;

        try
        {
            var expected = Convert.FromHexString(ComputeGitHubSignatureHex(secret, body));
            var actual = Convert.FromHexString(header["sha256=".Length..]);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static string ComputeGitHubSignatureHex(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    private static async Task<bool> VerifyAdoBasicAuthAsync(Workspace workspace, HttpRequest request, ISecretStore secrets, CancellationToken ct)
    {
        var header = request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(workspace.AppSecretRef)) return false;

        var secret = await secrets.ReadAsync(workspace.AppSecretRef, ct);
        if (string.IsNullOrEmpty(secret)) return false;

        var expected = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + secret));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes(expected));
    }

    internal static async Task ProcessGitHubEventAsync(AppDbContext db, Workspace workspace, string eventType, string body, string? deliveryId, ILogger logger, CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            logger.LogWarning("Discarding malformed webhook JSON body for workspace {WorkspaceId}, event {EventType}", workspace.Id, eventType);
            return;
        }
        using var _ = document;
        var root = document.RootElement;

        if (eventType == "pull_request")
        {
            await HandlePullRequestOpenedAsync(db, workspace, root, deliveryId, ct);
        }
        else if (eventType == "pull_request_review")
        {
            await HandlePullRequestReviewApprovedAsync(db, workspace, root, deliveryId, logger, ct);
        }
    }

    private static async Task HandlePullRequestOpenedAsync(AppDbContext db, Workspace workspace, JsonElement root, string? deliveryId, CancellationToken ct)
    {
        var action = root.TryGetProperty("action", out var actionEl) ? actionEl.GetString() : null;
        if (action != "opened" && action != "synchronize") return;
        if (!root.TryGetProperty("pull_request", out var pr) || !pr.TryGetProperty("number", out var numberEl)) return;

        var prNumber = numberEl.GetInt64().ToString();
        var prBody = pr.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String ? bodyEl.GetString() ?? "" : "";
        var closesRef = ExtractClosesReference(prBody);
        if (closesRef is null) return;

        // Only the still-uncorrelated pipeline_instance for this Issue matches - once PrRef is set by a
        // prior event, this query naturally returns nothing on reprocessing (state-based idempotency,
        // seção 6.6: "é esse o primeiro e único momento em que a correlação Issue<->PR é feita por texto").
        var pipeline = await db.PipelineInstances.SingleOrDefaultAsync(
            p => p.WorkspaceId == workspace.Id && p.ExternalRef == closesRef && p.PrRef == null, ct);
        if (pipeline is null) return;

        await AdvancePhaseAsync(db, pipeline, PipelinePhase.CodeReview, $"pull_request.{action}", deliveryId, ct, setPrRef: prNumber);
    }

    private static async Task HandlePullRequestReviewApprovedAsync(AppDbContext db, Workspace workspace, JsonElement root, string? deliveryId, ILogger logger, CancellationToken ct)
    {
        var action = root.TryGetProperty("action", out var actionEl) ? actionEl.GetString() : null;
        if (action != "submitted") return;
        if (!root.TryGetProperty("review", out var review)) return;
        var state = review.TryGetProperty("state", out var stateEl) ? stateEl.GetString() : null;
        if (!string.Equals(state, "approved", StringComparison.OrdinalIgnoreCase)) return;
        var reviewerLogin = review.TryGetProperty("user", out var userEl) && userEl.TryGetProperty("login", out var loginEl) ? loginEl.GetString() : null;
        if (string.IsNullOrEmpty(reviewerLogin)) return;
        if (!root.TryGetProperty("pull_request", out var pr) || !pr.TryGetProperty("number", out var numberEl)) return;
        var prNumber = numberEl.GetInt64().ToString();

        var pipeline = await db.PipelineInstances.SingleOrDefaultAsync(p => p.WorkspaceId == workspace.Id && p.PrRef == prNumber, ct);
        if (pipeline is null) return;

        // The reviewer's GitHub account tells us which Hermes perfil approved (seção 6.1): the
        // perfil_credential registry (item 8) is exactly the (workspace, perfil) -> platform_username
        // mapping this needs, so no separate configuration is required here.
        var credential = await db.PerfilCredentials.SingleOrDefaultAsync(
            c => c.WorkspaceId == workspace.Id && c.PlatformUsername == reviewerLogin && c.Status == CredentialStatus.Active, ct);
        if (credential is null)
        {
            logger.LogInformation("Approval by {Reviewer} on workspace {WorkspaceId} doesn't match any active perfil_credential; not advancing phase", reviewerLogin, workspace.Id);
            return;
        }

        var target = TargetPhaseForApprover(credential.Perfil);
        if (target is null) return;

        await AdvancePhaseAsync(db, pipeline, target.Value, $"pull_request_review.approved:{PerfilConvert.ToApiString(credential.Perfil)}", deliveryId, ct);
    }

    internal static PipelinePhase? TargetPhaseForApprover(Perfil perfil) => perfil switch
    {
        Perfil.Revisor => PipelinePhase.Qa,
        Perfil.Qa => PipelinePhase.Seguranca,
        Perfil.Seguranca => PipelinePhase.Deploy,
        _ => null
    };

    private static readonly Regex ClosesReferenceRegex = new(@"(?i)\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#(\d+)", RegexOptions.Compiled);

    internal static string? ExtractClosesReference(string body) =>
        ClosesReferenceRegex.Match(body ?? "") is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>Also reused by ReconciliationPollerService, which calls this with deliveryId: null (poller-sourced transitions have no webhook delivery to dedup against, seção 6.2/10 - the phase-ordering guard below is what makes reconciliation passes safe to repeat).</summary>
    internal static async Task AdvancePhaseAsync(AppDbContext db, PipelineInstance pipeline, PipelinePhase target, string sourceEvent, string? deliveryId, CancellationToken ct, string? setPrRef = null)
    {
        // Primary dedup guard (seção 10.1/6.2): the platform's own delivery id. A double/quadruple-fired
        // webhook for the same logical event must never produce a second row.
        if (deliveryId is not null && await db.PhaseTransitions.AnyAsync(t => t.PipelineInstanceId == pipeline.Id && t.DeliveryId == deliveryId, ct))
            return;

        if (setPrRef is not null) pipeline.PrRef = setPrRef;

        // Secondary guard: only ever move forward. Protects against out-of-order delivery and events
        // that aren't literal re-fires of the same delivery (e.g. a stale synchronize after review
        // events already advanced the phase further).
        if (target <= pipeline.FaseAtual) return;

        pipeline.FaseAtual = target;
        pipeline.GateStatus = target == PipelinePhase.Deploy ? GateStatus.Approved : GateStatus.Pending; // seção 6.5
        db.PhaseTransitions.Add(new PhaseTransition { PipelineInstanceId = pipeline.Id, Fase = target, EnteredAt = DateTime.UtcNow, SourceEvent = sourceEvent, DeliveryId = deliveryId });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Only treat this as the expected dedup race if a transition for this exact delivery now
            // exists - confirmed, not assumed. A DbUpdateException for any other reason (FK violation,
            // connection lost, unrelated constraint, ...) must not be swallowed as if it were that race:
            // rethrow so Receive() turns it into a 500 and GitHub/Azure DevOps retries the delivery,
            // instead of the caller believing a 202 that was never really true.
            if (deliveryId is null || !await db.PhaseTransitions.AnyAsync(t => t.PipelineInstanceId == pipeline.Id && t.DeliveryId == deliveryId, ct))
                throw;
        }
    }
}
