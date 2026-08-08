using Backend.Api.Apis;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Services;

/// <summary>
/// Periodic reconciliation (seção 6.2/15, 5 min default) against real platform state, correcting drift
/// from lost/duplicated webhooks: recreates pipeline_instance rows for spec_publication entries whose
/// local write never landed (seção 11), and re-derives fase_atual from the PR's actual current reviews
/// when a pull_request_review webhook was lost. Phase-drift recovery is GitHub only - Azure DevOps event
/// mapping isn't implemented yet (see WebhookEndpoints), so PlatformContentClient's ADO methods return
/// null and are skipped identically to a platform error.
/// </summary>
public sealed class ReconciliationPollerService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<ReconciliationPollerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = configuration.GetValue("Reconciliation:IntervalMinutes", 5);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes <= 0 ? 5 : minutes));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reconciliation pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformContentClient>();

        // Missing-pipeline-instance recovery is now a pure DB comparison (spec_publication vs
        // pipeline_instance - see ReconcileMissingPipelineInstancesAsync), so unlike phase-drift
        // recovery it isn't GitHub-specific and applies to every active workspace.
        var workspaces = await db.Workspaces.Where(w => w.Status == WorkspaceStatus.Active).ToListAsync(ct);

        foreach (var workspace in workspaces)
        {
            await ReconcileMissingPipelineInstancesAsync(db, workspace, ct);
            if (workspace.Platform == WorkspacePlatform.Github)
                await ReconcilePhaseDriftAsync(db, platform, workspace, ct);
        }
    }

    /// <summary>
    /// Recreates pipeline_instance rows for every spec_publication that doesn't have one yet - the
    /// durable, backend-only record of a "Subir US" publish (SpecUsEndpoints.Handle) is the sole source
    /// of truth here (Security review on PR #17): unlike a GitHub Issue's label or body, nothing
    /// external can create a spec_publication row, so there's nothing here for an outside collaborator
    /// to forge. No GitHub call, no pagination concern (QA review on PR #17) - just our own tables.
    /// </summary>
    private static async Task ReconcileMissingPipelineInstancesAsync(AppDbContext db, Workspace workspace, CancellationToken ct)
    {
        var missing = await db.SpecPublications
            .Where(p => p.WorkspaceId == workspace.Id)
            .Where(p => !db.PipelineInstances.Any(pi => pi.WorkspaceId == p.WorkspaceId && pi.ExternalRef == p.ExternalRef))
            .ToListAsync(ct);

        foreach (var publication in missing)
            await TryCreatePipelineInstanceAsync(db, workspace.Id, publication.ExternalRef, ct, publication.SpecId);
    }

    internal static async Task TryCreatePipelineInstanceAsync(AppDbContext db, long workspaceId, string externalRef, CancellationToken ct, long? specId = null)
    {
        db.PipelineInstances.Add(new PipelineInstance
        {
            WorkspaceId = workspaceId,
            SpecId = specId,
            FaseAtual = PipelinePhase.Requisitos,
            GateStatus = GateStatus.Approved,
            ExternalRef = externalRef,
            CreatedAt = DateTime.UtcNow
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            // Only treat this as the expected unique (WorkspaceId, ExternalRef) race - something else
            // (webhook, or another reconciliation pass) creating the same row concurrently - if that
            // row is now confirmed to exist. A DbUpdateException for any other reason (schema/
            // constraint mismatch, DB unavailable, ...) must not be swallowed as if it were that race:
            // rethrow so it surfaces via ExecuteAsync's error log instead of the missing
            // pipeline_instance silently never getting recreated.
            if (!await db.PipelineInstances.AnyAsync(p => p.WorkspaceId == workspaceId && p.ExternalRef == externalRef, ct))
                throw;
        }
    }

    private static async Task ReconcilePhaseDriftAsync(AppDbContext db, PlatformContentClient platform, Workspace workspace, CancellationToken ct)
    {
        var pipelines = await db.PipelineInstances
            .Where(p => p.WorkspaceId == workspace.Id && p.PrRef != null && p.FaseAtual != PipelinePhase.Deploy)
            .ToListAsync(ct);

        foreach (var pipeline in pipelines)
        {
            var approverLogins = await platform.ListApprovedReviewerLoginsAsync(workspace, pipeline.PrRef!, ct);
            if (approverLogins is null || approverLogins.Count == 0) continue;

            var credentials = await db.PerfilCredentials
                .Where(c => c.WorkspaceId == workspace.Id && c.Status == CredentialStatus.Active && approverLogins.Contains(c.PlatformUsername))
                .ToListAsync(ct);

            // Order doesn't matter: WebhookEndpoints.AdvancePhaseAsync only ever moves fase_atual
            // forward, so applying a lower-reaching approval after a higher-reaching one is a no-op.
            foreach (var credential in credentials)
            {
                var target = WebhookEndpoints.TargetPhaseForApprover(credential.Perfil);
                if (target is null) continue;
                await WebhookEndpoints.AdvancePhaseAsync(db, pipeline, target.Value, $"reconciliation:{PerfilConvert.ToApiString(credential.Perfil)}", deliveryId: null, ct);
            }
        }
    }
}
