using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Apis;

/// <summary>
/// Aggregate view for a workspace (seção 6.3/6.4): a count of pipeline_instance per fase, plus the
/// gates currently waiting on a human approval. Not a per-item Kanban view (seção 6.3: "nesta rodada").
/// </summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/workspaces/{id:long}/dashboard", Get);
        return app;
    }

    // seção 6.5: gate_status only ever carries "pending" meaning for a human once fase_atual reaches
    // Code Review - Design/Dev are reported (no PR to review yet), Requisitos is auto-approved at
    // "Subir US" click, and Deploy has no next platform gate. Table 4.3 of especificacao-hermes-sdlc.md.
    private static readonly IReadOnlyDictionary<PipelinePhase, (string Transicao, string Aprovador)> GateInfo = new Dictionary<PipelinePhase, (string, string)>
    {
        [PipelinePhase.CodeReview] = ("Code Review → QA", "Reviewer designado"),
        [PipelinePhase.Qa] = ("QA → Segurança", "QA Lead"),
        [PipelinePhase.Seguranca] = ("Segurança → Deploy", "AppSec + Release Manager")
    };

    private static async Task<IResult> Get(long id, AppDbContext db, CancellationToken ct)
    {
        var workspace = await db.Workspaces.SingleOrDefaultAsync(w => w.Id == id, ct);
        if (workspace is null) return Results.NotFound();

        var pipelines = await db.PipelineInstances.Where(p => p.WorkspaceId == id).ToListAsync(ct);

        var contagens = Enum.GetValues<PipelinePhase>().ToDictionary(f => f.ToString(), f => pipelines.Count(p => p.FaseAtual == f));

        var gatesPendentes = pipelines
            .Where(p => p.GateStatus == GateStatus.Pending && GateInfo.ContainsKey(p.FaseAtual))
            .Select(p =>
            {
                var (transicao, aprovador) = GateInfo[p.FaseAtual];
                return new
                {
                    pipeline_instance_id = p.Id,
                    external_ref = p.ExternalRef,
                    fase_atual = p.FaseAtual.ToString(),
                    transicao,
                    aprovador_esperado = aprovador,
                    deep_link = BuildDeepLink(workspace, p.PrRef)
                };
            })
            .ToList();

        return Results.Ok(new { contagens, gates_pendentes = gatesPendentes });
    }

    // seção 6.6: no deep-link without pr_ref yet (fases Requisitos/Design/Dev never reach here anyway,
    // since GateInfo only covers Code Review onward, but pr_ref could in principle still be unset).
    private static string? BuildDeepLink(Workspace workspace, string? prRef)
    {
        if (prRef is null) return null;

        if (workspace.Platform == WorkspacePlatform.Github)
            return $"https://github.com/{workspace.PlatformRef}/pull/{prRef}";

        var parts = workspace.CodeRepo?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 3 }) return null; // seção 10.1: code_repo for ADO is "org/project/repo"
        return $"https://dev.azure.com/{parts[0]}/{parts[1]}/_git/{parts[2]}/pullrequest/{prRef}";
    }
}
