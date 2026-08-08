using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Backend.Api.Services;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Apis;

public static class SpecListingEndpoints
{
    public static IEndpointRouteBuilder MapSpecListingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/workspaces/{id:long}/specs", List);
        return app;
    }

    private static async Task<IResult> List(long id, string? status, AppDbContext db, PlatformContentClient platform, CancellationToken ct)
    {
        var workspace = await db.Workspaces.SingleOrDefaultAsync(w => w.Id == id, ct);
        if (workspace is null) return Results.NotFound();

        var repo = workspace.SpecsRepo ?? workspace.PlatformRef;
        var directory = string.IsNullOrWhiteSpace(workspace.SpecsPath) ? "" : workspace.SpecsPath.TrimEnd('/');

        var files = await platform.ListMarkdownFilesAsync(workspace, repo, directory, ct);
        if (files is null) return Results.StatusCode(StatusCodes.Status502BadGateway);

        // On-demand sync into the `spec` index (section 3/5.2): reads the Status blockquote of every
        // .md file in the configured directory on each call and upserts `spec` by (workspace, path).
        // The spec describes this as a periodic background read; doing it synchronously here keeps the
        // listing correct without a separate scheduler, at the cost of one extra platform round-trip
        // per file on every request - acceptable at this pilot's scale (dozens of specs, seção 15).
        var now = DateTime.UtcNow;
        foreach (var path in files)
        {
            var content = await platform.FetchFileAsync(workspace, repo, path, ct);
            if (content is null) continue; // best-effort: one unreadable file must not fail the whole listing

            var parsedStatus = ParseStatus(content);
            if (parsedStatus is null) continue;
            var title = ParseTitle(content, path);

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
        await db.SaveChangesAsync(ct);

        var query = db.Specs.AsNoTracking().Where(s => s.WorkspaceId == id);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(s => s.Status == status);
        var specs = await query.OrderBy(s => s.Path)
            .Select(s => new SpecListItem(s.Path, s.Title, s.Status, s.Version, s.UpdatedAt))
            .ToListAsync(ct);
        return Results.Ok(specs);
    }

    internal static string? ParseStatus(string content)
    {
        // Convention shared with the `spec` skill: "> Status: <estado> (<data>)."
        var match = Regex.Match(content, @"(?m)^>\s*Status:\s*([^(\r\n]+?)\s*\(");
        return match.Success ? Slugify(match.Groups[1].Value) : null;
    }

    internal static string ParseTitle(string content, string path)
    {
        var match = Regex.Match(content, @"(?m)^#\s+(.+?)\s*$");
        var title = match.Success ? match.Groups[1].Value.Trim() : "";
        return title.Length > 0 ? title : System.IO.Path.GetFileNameWithoutExtension(path);
    }

    private static string Slugify(string raw)
    {
        // Normalizes accents/case so "Rascunho"/"rascunho"/etc. in the blockquote all map to the
        // same lowercase-ASCII domain value used by spec.status and the `status=` query filter.
        var normalized = raw.Trim().Normalize(NormalizationForm.FormD);
        var withoutDiacritics = new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return withoutDiacritics.ToLowerInvariant();
    }
}

public sealed record SpecListItem(string Path, string Title, string Status, int Version, DateTime UpdatedAt);
