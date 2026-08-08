using System.Text.Json.Serialization;
using Backend.Api.Services;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Apis;

public static class CredentialEndpoints
{
    public static IEndpointRouteBuilder MapCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/workspaces/{id:long}/credenciais", Create);
        app.MapGet("/workspaces/{id:long}/credenciais", List);
        return app;
    }

    private static async Task<IResult> Create(long id, CreateCredentialRequest? request, AppDbContext db, ISecretStore secrets, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var errors = Validate(request);
        if (errors.Count > 0) return Results.UnprocessableEntity(new { errors });

        var workspace = await db.Workspaces.SingleOrDefaultAsync(w => w.Id == id, ct);
        if (workspace is null) return Results.NotFound();

        var perfil = PerfilConvert.Parse(request!.Perfil)!.Value;
        var now = DateTime.UtcNow;

        string secretRef;
        try
        {
            // The key only has to be unique per write - it becomes part of the Kubernetes Secret name,
            // never the token value itself. A fresh key per rotation keeps the previous Secret intact
            // (and the previous PerfilCredential row's secret_ref still resolvable) until it's cleaned
            // up separately; the DB row is what actually marks a credential active/revoked.
            secretRef = await secrets.StoreAsync($"{workspace.Slug}-{PerfilConvert.ToApiString(perfil)}-{Guid.NewGuid():N}", request.Token!, ct);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        // Transaction pairs the revoke with the insert so a reader never observes zero active
        // credentials between the two writes. It does not by itself stop two concurrent requests from
        // both revoking the same previous row and both inserting a new "active" one - the unique
        // filtered index (IX_perfil_credential_active_per_perfil) is what actually rejects the second
        // writer; SQLite serializes the two transactions, and the second one's SaveChanges throws
        // DbUpdateException on the constraint, which is reported as 409 below.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        PerfilCredential credential;
        try
        {
            var previousActive = await db.PerfilCredentials
                .Where(c => c.WorkspaceId == id && c.Perfil == perfil && c.Status == CredentialStatus.Active)
                .ToListAsync(ct);
            foreach (var previous in previousActive)
            {
                previous.Status = CredentialStatus.Revoked;
                previous.RotatedAt = now;
            }

            credential = new PerfilCredential
            {
                WorkspaceId = id,
                Perfil = perfil,
                PlatformUsername = request.PlatformUsername!.Trim(),
                SecretRef = secretRef,
                Scopes = string.IsNullOrWhiteSpace(request.Scopes) ? null : request.Scopes.Trim(),
                Status = CredentialStatus.Active,
                CreatedAt = now
            };
            db.PerfilCredentials.Add(credential);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // Unambiguous: CommitAsync was never reached, so no row referencing secretRef can possibly
            // exist yet - safe to compensate regardless of which exception this was (constraint
            // violation, cancellation, connection loss before commit, ...), not just DbUpdateException.
            await TryCleanupOrphanedSecretAsync(secrets, secretRef, id, perfil, ex, loggerFactory.CreateLogger("CredentialEndpoints"), ct);
            return ex is DbUpdateException
                ? Results.Conflict(new { error = "a credential for this perfil was registered concurrently; retry" })
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        try
        {
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // Ambiguous, deliberately NOT cleaned up: SaveChangesAsync succeeded, but this exception
            // means we don't know whether COMMIT itself actually reached and applied before it surfaced.
            // Deleting the secret here risks orphaning the row the other way instead (a committed
            // PerfilCredential pointing at a now-deleted Secret, which is worse - the row would look
            // valid but the token behind it would be gone). Left for a future reconciliation pass,
            // same "external/DB write partially failed" philosophy already accepted for Issue/
            // pipeline_instance correlation (seção 11) rather than risking that swap.
            loggerFactory.CreateLogger("CredentialEndpoints").LogError(ex,
                "Commit failed after SaveChanges for workspace {WorkspaceId} perfil {Perfil}; secret {SecretRef} left as-is because the commit outcome is ambiguous",
                id, perfil, secretRef);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        return Results.Created($"/workspaces/{id}/credenciais/{credential.Id}", Response(credential));
    }

    internal static async Task TryCleanupOrphanedSecretAsync(ISecretStore secrets, string secretRef, long workspaceId, Perfil perfil, Exception cause, ILogger logger, CancellationToken ct)
    {
        // Best-effort: a cleanup failure must not override the original error being reported to the
        // caller, just be visible for manual follow-up.
        try
        {
            await secrets.DeleteAsync(secretRef, ct);
        }
        catch (Exception cleanupEx)
        {
            logger.LogError(cleanupEx,
                "Failed to clean up orphaned secret {SecretRef} for workspace {WorkspaceId} perfil {Perfil} after {Cause}",
                secretRef, workspaceId, perfil, cause.Message);
        }
    }

    private static async Task<IResult> List(long id, AppDbContext db, CancellationToken ct)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == id, ct)) return Results.NotFound();

        var credentials = await db.PerfilCredentials.AsNoTracking()
            .Where(c => c.WorkspaceId == id)
            .OrderBy(c => c.Perfil).ThenByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(credentials.Select(Response));
    }

    private static List<string> Validate(CreateCredentialRequest? request)
    {
        var errors = new List<string>();
        if (request is null)
        {
            errors.Add("request: is required");
            return errors;
        }
        if (PerfilConvert.Parse(request.Perfil) is null)
            errors.Add("perfil: must be one of analista_requisitos, arquiteto, dev, revisor, qa, seguranca, release_deploy");
        if (string.IsNullOrWhiteSpace(request.PlatformUsername))
            errors.Add("platform_username: is required");
        if (string.IsNullOrWhiteSpace(request.Token))
            errors.Add("token: is required");
        return errors;
    }

    private static string StatusToString(CredentialStatus status) => status switch
    {
        CredentialStatus.Active => "active",
        CredentialStatus.Revoked => "revoked",
        _ => "pending"
    };

    // Deliberately excludes the token: only secret_ref (a pointer into the Kubernetes Secret, not the
    // value) is ever exposed, matching the write-only guarantee in seção 8 - the field simply never
    // reaches this projection, so there is no field to accidentally serialize back to the client.
    private static CredentialResponse Response(PerfilCredential c) =>
        new(c.Id, c.WorkspaceId, PerfilConvert.ToApiString(c.Perfil), c.PlatformUsername, c.SecretRef, c.Scopes, StatusToString(c.Status), c.CreatedAt, c.RotatedAt);
}

public sealed record CreateCredentialRequest(
    [property: JsonPropertyName("perfil")] string? Perfil,
    [property: JsonPropertyName("platform_username")] string? PlatformUsername,
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("scopes")] string? Scopes);

public sealed record CredentialResponse(long Id, long WorkspaceId, string Perfil, string PlatformUsername, string SecretRef, string? Scopes, string Status, DateTime CreatedAt, DateTime? RotatedAt);
