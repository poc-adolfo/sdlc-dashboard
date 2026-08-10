using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Backend.Api.Services;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Apis;

/// <summary>
/// Specs now live in blob storage instead of the workspace's Git repo (spec 2026-08-09 update, seção
/// 5.2) - path convention {client_id}/{projeto}/{fileName} (ISpecStorage). "projeto" has no database
/// row of its own, it's purely a storage prefix; workspace.ClientId (set once the assessment concludes,
/// AssessmentEndpoints.Conclude) is what scopes every call here to the right top-level folder, so every
/// route requires it to already be set.
/// </summary>
public static class SpecStorageEndpoints
{
    public static IEndpointRouteBuilder MapSpecStorageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/workspaces/{id:long}/spec-projects", ListProjects);
        app.MapPost("/workspaces/{id:long}/spec-projects", CreateProject);
        app.MapGet("/workspaces/{id:long}/spec-projects/{projeto}/specs", ListSpecs);
        app.MapGet("/workspaces/{id:long}/spec-projects/{projeto}/specs/{fileName}", GetSpecContent);
        app.MapPut("/workspaces/{id:long}/spec-projects/{projeto}/specs/{fileName}", SaveSpecContent);
        app.MapPost("/workspaces/{id:long}/spec-projects/{projeto}/specs/{fileName}/chat", Chat);
        return app;
    }

    private static async Task<(Workspace? Workspace, IResult? Error)> ResolveWorkspace(long id, AppDbContext db, CancellationToken ct)
    {
        var workspace = await db.Workspaces.SingleOrDefaultAsync(w => w.Id == id, ct);
        if (workspace is null) return (null, Results.NotFound());
        if (workspace.ClientId is null)
            return (null, Results.UnprocessableEntity(new { errors = new[] { "workspace: conclua o assessment antes de acessar specs (client_id ainda não definido)" } }));
        return (workspace, null);
    }

    private static async Task<IResult> ListProjects(long id, AppDbContext db, ISpecStorage storage, CancellationToken ct)
    {
        var (workspace, error) = await ResolveWorkspace(id, db, ct);
        if (error is not null) return error;
        var projects = await storage.ListProjectsAsync(workspace!.ClientId!.Value.ToString(CultureInfo.InvariantCulture), ct);
        return Results.Ok(projects);
    }

    private static async Task<IResult> CreateProject(long id, CreateProjectRequest? request, AppDbContext db, ISpecStorage storage, CancellationToken ct)
    {
        var (workspace, error) = await ResolveWorkspace(id, db, ct);
        if (error is not null) return error;
        var name = request?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || !SpecStoragePathSegment.IsValid(name))
            return Results.UnprocessableEntity(new { errors = new[] { "name: obrigatório, sem \"/\", \"\\\" ou \"..\", até 200 caracteres" } });
        await storage.CreateProjectAsync(workspace!.ClientId!.Value.ToString(CultureInfo.InvariantCulture), name, ct);
        return Results.Created($"/workspaces/{id}/spec-projects/{Uri.EscapeDataString(name)}/specs", new { name });
    }

    private static async Task<IResult> ListSpecs(long id, string projeto, AppDbContext db, ISpecStorage storage, CancellationToken ct)
    {
        if (!SpecStoragePathSegment.IsValid(projeto)) return Results.NotFound();
        var (workspace, error) = await ResolveWorkspace(id, db, ct);
        if (error is not null) return error;
        var clientId = workspace!.ClientId!.Value.ToString(CultureInfo.InvariantCulture);

        var files = await storage.ListSpecFilesAsync(clientId, projeto, ct);

        // On-demand sync into the `spec` index (mirrors the pre-2026-08-09 git-based listing's
        // behavior, just reading file content from storage instead of the platform's Contents API) - the
        // index is what pipeline_instance.SpecId/SpecPublication correlate against, not the storage
        // listing itself.
        var now = DateTime.UtcNow;
        var pathPrefix = projeto + "/";
        foreach (var fileName in files)
        {
            var content = await storage.GetContentAsync(clientId, projeto, fileName, ct);
            if (content is null) continue; // best-effort: one unreadable file must not fail the whole listing
            var parsedStatus = ParseStatus(content);
            if (parsedStatus is null) continue;
            var title = ParseTitle(content, fileName);
            var path = pathPrefix + fileName;

            var spec = await db.Specs.SingleOrDefaultAsync(s => s.WorkspaceId == id && s.Path == path, ct);
            if (spec is null)
            {
                db.Specs.Add(new Spec { WorkspaceId = id, Path = path, Title = title, Status = parsedStatus, Version = 1, CreatedAt = now, UpdatedAt = now });
            }
            else if (spec.Status != parsedStatus || spec.Title != title)
            {
                spec.Status = parsedStatus;
                spec.Title = title;
                spec.Version += 1;
                spec.UpdatedAt = now;
            }
        }

        var currentPaths = files.Select(f => pathPrefix + f).ToHashSet();
        var staleSpecs = await db.Specs
            .Where(s => s.WorkspaceId == id && s.Path.StartsWith(pathPrefix) && !db.PipelineInstances.Any(p => p.SpecId == s.Id))
            .ToListAsync(ct);
        db.Specs.RemoveRange(staleSpecs.Where(s => !currentPaths.Contains(s.Path)));
        await db.SaveChangesAsync(ct);

        var indexed = await db.Specs.AsNoTracking().Where(s => s.WorkspaceId == id && s.Path.StartsWith(pathPrefix)).ToDictionaryAsync(s => s.Path, ct);
        var items = files.Select(fileName =>
        {
            indexed.TryGetValue(pathPrefix + fileName, out var spec);
            return new SpecFileItem(fileName, spec?.Title ?? System.IO.Path.GetFileNameWithoutExtension(fileName), spec?.Status, spec?.Version ?? 0, spec?.UpdatedAt);
        }).ToList();
        return Results.Ok(items);
    }

    private static async Task<IResult> GetSpecContent(long id, string projeto, string fileName, AppDbContext db, ISpecStorage storage, CancellationToken ct)
    {
        if (!SpecStoragePathSegment.IsValid(projeto) || !SpecStoragePathSegment.IsValid(fileName)) return Results.NotFound();
        var (workspace, error) = await ResolveWorkspace(id, db, ct);
        if (error is not null) return error;
        var content = await storage.GetContentAsync(workspace!.ClientId!.Value.ToString(CultureInfo.InvariantCulture), projeto, fileName, ct);
        return content is null ? Results.NotFound() : Results.Text(content, "text/markdown; charset=utf-8");
    }

    private static async Task<IResult> SaveSpecContent(long id, string projeto, string fileName, SaveSpecContentRequest? request, AppDbContext db, ISpecStorage storage, CancellationToken ct)
    {
        if (!SpecStoragePathSegment.IsValid(projeto) || !SpecStoragePathSegment.IsValid(fileName)) return Results.NotFound();
        if (request?.Content is null) return Results.UnprocessableEntity(new { errors = new[] { "content: is required" } });
        var (workspace, error) = await ResolveWorkspace(id, db, ct);
        if (error is not null) return error;
        await storage.SaveContentAsync(workspace!.ClientId!.Value.ToString(CultureInfo.InvariantCulture), projeto, fileName, request.Content, ct);
        return Results.Ok(new { saved = true });
    }

    private static async Task<IResult> Chat(long id, string projeto, string fileName, ChatRequest? request, AppDbContext db, SpecsSkillChatClient client, CancellationToken ct)
    {
        if (!SpecStoragePathSegment.IsValid(projeto) || !SpecStoragePathSegment.IsValid(fileName)) return Results.NotFound();
        if (request?.Messages is null || request.Messages.Count == 0)
            return Results.UnprocessableEntity(new { errors = new[] { "messages: is required" } });
        var (_, error) = await ResolveWorkspace(id, db, ct);
        if (error is not null) return error;

        var reply = await client.SendAsync(request.Messages.Select(m => new ChatMessage(m.Role, m.Content)).ToList(), ct);
        return reply is null ? Results.StatusCode(StatusCodes.Status502BadGateway) : Results.Ok(new { reply });
    }

    // Same "> Status: <estado> (<data>)." convention the `spec` skill already writes (shared with the
    // pre-2026-08-09 git-based listing this replaces).
    internal static string? ParseStatus(string content)
    {
        var match = Regex.Match(content, @"(?m)^>\s*Status:\s*([^(\r\n]+?)\s*\(");
        return match.Success ? Slugify(match.Groups[1].Value) : null;
    }

    internal static string ParseTitle(string content, string fileName)
    {
        var match = Regex.Match(content, @"(?m)^#\s+(.+?)\s*$");
        var title = match.Success ? match.Groups[1].Value.Trim() : "";
        return title.Length > 0 ? title : System.IO.Path.GetFileNameWithoutExtension(fileName);
    }

    private static string Slugify(string raw)
    {
        var normalized = raw.Trim().Normalize(NormalizationForm.FormD);
        var withoutDiacritics = new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return withoutDiacritics.ToLowerInvariant();
    }
}

public sealed record CreateProjectRequest([property: JsonPropertyName("name")] string? Name);
public sealed record SaveSpecContentRequest([property: JsonPropertyName("content")] string? Content);
public sealed record ChatMessageDto([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);
public sealed record ChatRequest([property: JsonPropertyName("messages")] List<ChatMessageDto>? Messages);
public sealed record SpecFileItem(string FileName, string Title, string? Status, int Version, DateTime? UpdatedAt);
