using System.Text.Json.Serialization;
using Backend.Api.Services;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Apis;

public static class AssessmentEndpoints
{
    public const int MaxClientNameLength = 200;

    public const string DefaultContent = "## Linha de negocio do cliente\n\n\n## Stack utilizada\n\n\n## Arquiteturas presentes\n\n\n## Constraints de seguranca\n\n\n## Observacoes adicionais\n";

    public static IEndpointRouteBuilder MapAssessmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/clients", SearchClients);
        endpoints.MapGet("/workspaces/{id:long}/assessments/current", GetCurrent);
        endpoints.MapGet("/workspaces/{id:long}/assessments/{aid:long}", Get);
        endpoints.MapPost("/workspaces/{id:long}/assessments", Upsert);
        endpoints.MapPost("/workspaces/{id:long}/assessments/{aid:long}/concluir", Conclude);
        return endpoints;
    }

    private static async Task<IResult> Get(long id, long aid, AppDbContext db, CancellationToken ct)
    {
        var assessment = await db.Assessments.AsNoTracking().Include(a => a.Client).SingleOrDefaultAsync(a => a.Id == aid, ct);
        return assessment is null || assessment.WorkspaceId != id ? Results.NotFound() : Results.Ok(AssessmentResponse.From(assessment));
    }

    // Lets WorkspacePage restore the assessment (client + content) after a reload or a return visit,
    // instead of always dropping back to the empty ClientPicker even though everything was already
    // saved - the assessment_id needed for GET .../{aid} isn't known to the frontend until then, so it
    // has nowhere else to get it from.
    //
    // Bug fix: this used to filter to Status == EmAndamento only, which defeated its own purpose for
    // any workspace that had already been through "Concluir" once - Conclude flips the row to
    // Concluido, so a reload right after concluding (or any later visit) found nothing here and the
    // form reset to empty/template, silently discarding the client selection and content on screen even
    // though the database still had them. The most recent assessment regardless of status is always the
    // right one to restore - "em andamento" or "concluido" are both valid states to keep editing from.
    private static async Task<IResult> GetCurrent(long id, AppDbContext db, CancellationToken ct)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == id, ct)) return Results.NotFound();
        var assessment = await db.Assessments.AsNoTracking().Include(a => a.Client)
            .Where(a => a.WorkspaceId == id)
            .OrderByDescending(a => a.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        return assessment is null ? Results.NotFound() : Results.Ok(AssessmentResponse.From(assessment));
    }

    private static async Task<IResult> SearchClients(string? q, AppDbContext db, CancellationToken ct)
    {
        var query = db.Clients.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(c => c.Name.Contains(q.Trim()));
        var clients = await query.OrderBy(c => c.Name).Select(c => new ClientResponse(c.Id, c.Name)).ToListAsync(ct);
        return Results.Ok(clients);
    }

    private static async Task<IResult> Upsert(long id, AssessmentRequest? request, AppDbContext db, IConfiguration configuration, IBlobStore blobs, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (request is null || request.ClientId is null && string.IsNullOrWhiteSpace(request.ClientName))
            return Results.UnprocessableEntity(new { errors = new[] { "client_id or client_name is required" } });
        var maxContentLength = configuration.GetValue("Analista:MaxContentLength", 10000);
        if (maxContentLength <= 0) maxContentLength = 10000;
        var content = request.Content ?? DefaultContent;
        if (content.Length > maxContentLength)
            return Results.UnprocessableEntity(new { errors = new[] { $"content: maximum length is {maxContentLength} characters" } });
        if (request.ClientName is not null && request.ClientName.Trim().Length > MaxClientNameLength)
            return Results.UnprocessableEntity(new { errors = new[] { $"client_name: maximum length is {MaxClientNameLength} characters" } });
        var workspace = await db.Workspaces.SingleOrDefaultAsync(w => w.Id == id, ct);
        if (workspace is null) return Results.NotFound();
        Client? client = request.ClientId.HasValue ? await db.Clients.SingleOrDefaultAsync(c => c.Id == request.ClientId, ct) : null;
        if (request.ClientId.HasValue && client is null) return Results.UnprocessableEntity(new { errors = new[] { "client_id: client does not exist" } });
        if (client is null)
        {
            var name = request.ClientName!.Trim();
            if (name.Length == 0) return Results.UnprocessableEntity(new { errors = new[] { "client_name: is required" } });
            client = await db.Clients.SingleOrDefaultAsync(c => c.Name == name, ct);
            if (client is null) { client = new Client { Name = name, CreatedAt = DateTime.UtcNow }; db.Clients.Add(client); }
        }
        Assessment? assessment;
        if (request.AssessmentId.HasValue)
        {
            // An explicit assessment id is still scoped to the workspace in the URL.
            assessment = await db.Assessments.SingleOrDefaultAsync(a => a.Id == request.AssessmentId.Value, ct);
            if (assessment is null || assessment.WorkspaceId != id) return Results.NotFound();
        }
        else
        {
            assessment = await db.Assessments.SingleOrDefaultAsync(a => a.WorkspaceId == id && a.Status == AssessmentStatus.EmAndamento, ct);
        }
        var now = DateTime.UtcNow;
        if (assessment is null)
        {
            // A fresh revision row (workspace already concluded once, no explicit assessment_id given)
            // still needs to carry forward the design system already chosen for this workspace (seção
            // 4.2) - otherwise every re-save after "Concluir" would silently drop that selection even
            // though nothing about it changed.
            var previous = await db.Assessments.AsNoTracking().Where(a => a.WorkspaceId == id).OrderByDescending(a => a.UpdatedAt).FirstOrDefaultAsync(ct);
            assessment = new Assessment { WorkspaceId = id, Client = client, ClientId = client.Id, Content = content, FigmaProjectUrl = request.FigmaProjectUrl, SelectedDesignSystemProposalId = previous?.SelectedDesignSystemProposalId, CreatedAt = now, UpdatedAt = now };
            db.Assessments.Add(assessment);
        }
        else { assessment.Client = client; assessment.ClientId = client.Id; if (request.Content is not null) assessment.Content = request.Content; if (request.FigmaProjectUrl is not null) assessment.FigmaProjectUrl = request.FigmaProjectUrl; assessment.UpdatedAt = now; }
        await db.SaveChangesAsync(ct);

        // Best-effort: the database row is the system of record (see AssessmentResponse below), this is
        // a supplementary export. A blob store outage must not turn a successful save into a 502 -
        // Salvar already worked from the operator's point of view by the time SaveChangesAsync returned.
        try
        {
            await blobs.WriteAsync($"assessments/{workspace.Slug}/{assessment.Id}.md", assessment.Content, ct);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            loggerFactory.CreateLogger("AssessmentEndpoints").LogWarning(ex,
                "Failed to write markdown export for assessment {AssessmentId} (workspace {WorkspaceId})", assessment.Id, id);
        }

        return Results.Ok(AssessmentResponse.From(assessment));
    }

    private static async Task<IResult> Conclude(long id, long aid, AppDbContext db, CancellationToken ct)
    {
        // Keep the ownership check explicit: a mismatched workspace must look identical to a missing assessment.
        var assessment = await db.Assessments.SingleOrDefaultAsync(a => a.Id == aid, ct);
        if (assessment is null || assessment.WorkspaceId != id) return Results.NotFound();

        // Correção 2026-08-09 (spec seção 5.1/6.1): "Concluir" só persiste - não chama nenhum perfil
        // Hermes. O gate de DoR do Analista roda exclusivamente em "Subir US" (SpecUsEndpoints), sobre a
        // spec, não sobre o assessment.
        assessment.Status = AssessmentStatus.Concluido;
        var workspace = await db.Workspaces.SingleAsync(w => w.Id == id, ct);
        workspace.ClientId = assessment.ClientId;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { concluido = true });
    }
}

public sealed record AssessmentRequest([property: JsonPropertyName("assessment_id")] long? AssessmentId = null, [property: JsonPropertyName("client_id")] long? ClientId = null, [property: JsonPropertyName("client_name")] string? ClientName = null, [property: JsonPropertyName("content")] string? Content = null, [property: JsonPropertyName("figma_project_url")] string? FigmaProjectUrl = null);
public sealed record ClientResponse(long Id, string Name);
public sealed record AssessmentResponse(long Id, long WorkspaceId, long ClientId, string ClientName, string Content, string? FigmaProjectUrl, long? SelectedDesignSystemProposalId, string Status, DateTime CreatedAt, DateTime UpdatedAt)
{
    public static AssessmentResponse From(Assessment a) => new(a.Id, a.WorkspaceId, a.ClientId, a.Client?.Name ?? string.Empty, a.Content, a.FigmaProjectUrl, a.SelectedDesignSystemProposalId, a.Status == AssessmentStatus.Concluido ? "concluido" : "em_andamento", a.CreatedAt, a.UpdatedAt);
}
