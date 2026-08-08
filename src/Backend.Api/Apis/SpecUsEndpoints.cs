using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Backend.Api.Services;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Apis;

public static class SpecUsEndpoints
{
    public static IEndpointRouteBuilder MapSpecUsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/workspaces/{id:long}/specs/{**path}", Handle);
        return app;
    }

    private static async Task<IResult> Handle(long id, string path, AppDbContext db, PlatformContentClient platform, AnalystDorGate gate, ILoggerFactory logs, CancellationToken ct)
    {
        if (!path.EndsWith("/subir-us", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
        var specPath = path[..^"/subir-us".Length].TrimStart('/');

        var workspace = await db.Workspaces.SingleOrDefaultAsync(w => w.Id == id, ct);
        if (workspace is null) return Results.NotFound();

        var fullPath = string.IsNullOrWhiteSpace(workspace.SpecsPath) ? specPath : workspace.SpecsPath.TrimEnd('/') + "/" + specPath;
        var repo = workspace.SpecsRepo ?? workspace.PlatformRef;

        var fetched = await platform.FetchFileAsync(workspace, repo, fullPath, ct);
        if (fetched is null) return Results.StatusCode(StatusCodes.Status502BadGateway);

        var dor = await gate.CheckAsync(fetched, ct);
        if (dor is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
        if (!dor.Attended) return Results.Ok(new { dor_atendido = false, pendencias = dor.Pending });

        var body = Extract(fetched, logs.CreateLogger("SpecUsEndpoints"));
        var title = Regex.Match(fetched, @"(?m)^#\s+(.+?)\s*$").Groups[1].Value.Trim();
        if (title.Length == 0) title = "Sem titulo";
        if (workspace.SpecsRepo is not null && !string.Equals(workspace.SpecsRepo, workspace.PlatformRef, StringComparison.OrdinalIgnoreCase))
            body += $"\n\n---\n\n<details>\n<summary>Spec completa: {specPath}</summary>\n\n```markdown\n{fetched}\n```\n\n</details>";

        var external = await Publish(workspace, title, body, fullPath, platform, ct);
        if (external is null) return Results.StatusCode(StatusCodes.Status502BadGateway);

        // Spec.Path in the index (SpecListingEndpoints) is always the full repo-relative path, same as
        // fullPath here - not specPath, which is only the route segment after specs_path is stripped.
        var spec = await db.Specs.SingleOrDefaultAsync(x => x.WorkspaceId == id && x.Path == fullPath, ct);
        var pipeline = new PipelineInstance { WorkspaceId = id, SpecId = spec?.Id, FaseAtual = PipelinePhase.Requisitos, GateStatus = GateStatus.Approved, ExternalRef = external, CreatedAt = DateTime.UtcNow };
        db.PipelineInstances.Add(pipeline);
        await db.SaveChangesAsync(ct);
        return Results.Json(new { pipeline_instance = new PipelineInstanceResponse(pipeline.Id, pipeline.WorkspaceId, pipeline.SpecId, pipeline.FaseAtual.ToString(), pipeline.GateStatus.ToString(), pipeline.ExternalRef, pipeline.PrRef, pipeline.CreatedAt) }, statusCode: 201);
    }

    private static async Task<string?> Publish(Workspace workspace, string title, string body, string specPath, PlatformContentClient platform, CancellationToken ct)
    {
        try
        {
            HttpRequestMessage request;
            if (workspace.Platform == WorkspacePlatform.Github)
            {
                // The "sdlc-dashboard:spec=" marker and "sdlc-pipeline" label are how
                // ReconciliationPollerService later recognizes an Issue as something this backend
                // actually created (seção 6.2), not just any Issue a repo collaborator happens to label
                // "sdlc-pipeline" themselves (Security review on PR #16). Reconciliation cross-checks
                // the marker's spec path against the `spec` table before trusting it, so copying the
                // marker text alone isn't enough to forge a pipeline instance unless it also names a
                // spec this workspace actually has.
                var markedBody = body + $"\n\n<!-- sdlc-dashboard:spec={specPath} -->";
                request = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/repos/{workspace.PlatformRef}/issues")
                {
                    Content = JsonContent.Create(new { title = "US: " + title, body = markedBody, labels = new[] { "sdlc-pipeline" } })
                };
            }
            else
            {
                var parts = workspace.PlatformRef.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) return null;
                var type = Uri.EscapeDataString(workspace.AdoWorkItemType ?? "User Story");
                request = new HttpRequestMessage(HttpMethod.Post, $"https://dev.azure.com/{parts[0]}/{parts[1]}/_apis/wit/workitems/${type}?api-version=7.1")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[]
                    {
                        new { op = "add", path = "/fields/System.Title", value = "US: " + title },
                        new { op = "add", path = "/fields/System.Description", value = Html(body) }
                    }), Encoding.UTF8, "application/json-patch+json")
                };
            }
            platform.AddAuth(request, workspace.Platform);
            using var response = await platform.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return (workspace.Platform == WorkspacePlatform.Github ? result.GetProperty("number") : result.GetProperty("id")).ToString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    internal static string Extract(string spec, ILogger logger)
    {
        var userStory = Regex.Match(spec, @"(?ms)^##\s+User Story\s*\n(?<x>.*?)(?=^##\s|\z)", RegexOptions.Multiline).Groups["x"].Value.Trim();
        var criteria = Regex.Match(spec, @"(?ms)^##\s+Criterios de aceite\s*\n(?<x>.*?)(?=^##\s|\z)", RegexOptions.Multiline).Groups["x"].Value.Trim();
        var wbs = Regex.Match(spec, @"(?ms)^##\s+WBS[^\n]*\n(?<x>.*?)(?=^##\s|\z)", RegexOptions.Multiline).Groups["x"].Value.Trim();
        if (userStory == "") logger.LogWarning("Spec extraction section missing: User Story");
        if (criteria == "") logger.LogWarning("Spec extraction section missing: Criterios de aceite");
        if (wbs == "") logger.LogWarning("Spec extraction section missing: WBS");
        return $"## User Story\n{userStory}\n\n## Criterios de aceite\n{criteria}\n\n## WBS - Plano de implementacao\n{wbs}";
    }

    internal static string Html(string extracted)
    {
        var sb = new StringBuilder();
        foreach (var line in extracted.Split('\n'))
        {
            if (line.StartsWith("## "))
                sb.Append("<h2>").Append(System.Net.WebUtility.HtmlEncode(line[3..])).Append("</h2>");
            else if (line.StartsWith("- "))
                sb.Append("<li>").Append(Regex.Replace(System.Net.WebUtility.HtmlEncode(line[2..]), @"\*\*(.+?)\*\*", "<strong>$1</strong>")).Append("</li>");
            else if (line.Trim() != "")
                sb.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(line)).Append("</p>");
        }
        return sb.ToString();
    }
}

// Projects PipelineInstance to avoid serializing the EF-tracked entity directly: the Workspace
// navigation gets fixed up in-memory to the already-tracked workspace loaded earlier in Handle,
// and Workspace.PipelineInstances points back at this same instance, so System.Text.Json walks
// Workspace <-> PipelineInstances forever and throws (exceeds the default max depth).
public sealed record PipelineInstanceResponse(long Id, long WorkspaceId, long? SpecId, string FaseAtual, string GateStatus, string ExternalRef, string? PrRef, DateTime CreatedAt);
