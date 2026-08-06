using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Apis;

public static class AssessmentEndpoints
{
    public const string DefaultContent = "## Linha de negocio do cliente\n\n\n## Stack utilizada\n\n\n## Arquiteturas presentes\n\n\n## Constraints de seguranca\n\n\n## Observacoes adicionais\n";

    public static IEndpointRouteBuilder MapAssessmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/clients", SearchClients);
        endpoints.MapGet("/workspaces/{id:long}/assessments/{aid:long}", Get);
        endpoints.MapPost("/workspaces/{id:long}/assessments", Upsert);
        endpoints.MapPost("/workspaces/{id:long}/assessments/{aid:long}/concluir", Conclude);
        return endpoints;
    }

    private static async Task<IResult> Get(long id, long aid, AppDbContext db, CancellationToken ct)
    {
        var assessment = await db.Assessments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == aid, ct);
        return assessment is null || assessment.WorkspaceId != id ? Results.NotFound() : Results.Ok(AssessmentResponse.From(assessment));
    }

    private static async Task<IResult> SearchClients(string? q, AppDbContext db, CancellationToken ct)
    {
        var query = db.Clients.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(c => c.Name.Contains(q.Trim()));
        var clients = await query.OrderBy(c => c.Name).Select(c => new ClientResponse(c.Id, c.Name)).ToListAsync(ct);
        return Results.Ok(clients);
    }

    private static async Task<IResult> Upsert(long id, AssessmentRequest? request, AppDbContext db, CancellationToken ct)
    {
        if (request is null || request.ClientId is null && string.IsNullOrWhiteSpace(request.ClientName))
            return Results.UnprocessableEntity(new { errors = new[] { "client_id or client_name is required" } });
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
        if (assessment is null) { assessment = new Assessment { WorkspaceId = id, Client = client, ClientId = client.Id, Content = request.Content ?? DefaultContent, CreatedAt = now, UpdatedAt = now }; db.Assessments.Add(assessment); }
        else { assessment.Client = client; assessment.ClientId = client.Id; if (request.Content is not null) assessment.Content = request.Content; assessment.UpdatedAt = now; }
        await db.SaveChangesAsync(ct);
        return Results.Ok(AssessmentResponse.From(assessment));
    }

    private static async Task<IResult> Conclude(long id, long aid, AppDbContext db, IHttpClientFactory clients, IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        // Keep the ownership check explicit: a mismatched workspace must look identical to a missing assessment.
        var assessment = await db.Assessments.SingleOrDefaultAsync(a => a.Id == aid, ct);
        if (assessment is null || assessment.WorkspaceId != id) return Results.NotFound();
        var maxContentLength = configuration.GetValue("Analista:MaxContentLength", 10000);
        if (maxContentLength <= 0) maxContentLength = 10000;
        if (assessment.Content.Length > maxContentLength)
            return Results.UnprocessableEntity(new { errors = new[] { $"content: maximum length is {maxContentLength} characters" } });
        var baseUrl = configuration["Analista:ApiServerBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            loggerFactory.CreateLogger("AssessmentEndpoints").LogError("Analista:ApiServerBaseUrl is not configured");
            return Results.Problem("Analista:ApiServerBaseUrl is not configured", statusCode: StatusCodes.Status502BadGateway);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "v1/chat/completions"));
        request.Content = JsonContent.Create(new { model = "analista", messages = new[] { new { role = "user", content = assessment.Content } });
        var apiKey = configuration["Analista:ApiServerApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var response = await clients.CreateClient("Analista").SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var text = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
            var gate = ParseLastGateObject(text);
            if (gate is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            if (!gate.Value.Attended) return Results.Ok(new { concluido = false, pendencias = gate.Value.Pending });
            assessment.Status = AssessmentStatus.Concluido;
            var workspace = await db.Workspaces.SingleAsync(w => w.Id == id, ct);
            workspace.ClientId = assessment.ClientId;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { concluido = true });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            loggerFactory.CreateLogger("AssessmentEndpoints").LogWarning(ex, "Analista DoR gate failed for assessment {AssessmentId}", aid);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    internal static (bool Attended, List<string> Pending)? ParseLastGateObject(string text)
    {
        for (var end = text.Length; end > 0; end--)
        {
            var start = text.LastIndexOf('{', end - 1);
            if (start < 0) break;
            try
            {
                using var json = JsonDocument.Parse(text[start..end]);
                var root = json.RootElement;
                if (!root.TryGetProperty("dor_atendido", out var attended) ||
                    (attended.ValueKind != JsonValueKind.True && attended.ValueKind != JsonValueKind.False) ||
                    !root.TryGetProperty("pendencias", out var pending) ||
                    pending.ValueKind != JsonValueKind.Array)
                    continue;
                var values = new List<string>();
                foreach (var item in pending.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) return null;
                    values.Add(item.GetString()!);
                }
                return (attended.GetBoolean(), values);
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { return null; }
        }
        return null;
    }
}

public sealed record AssessmentRequest([property: JsonPropertyName("assessment_id")] long? AssessmentId = null, [property: JsonPropertyName("client_id")] long? ClientId = null, [property: JsonPropertyName("client_name")] string? ClientName = null, [property: JsonPropertyName("content")] string? Content = null);
public sealed record ClientResponse(long Id, string Name);
public sealed record AssessmentResponse(long Id, long WorkspaceId, long ClientId, string Content, string Status, DateTime CreatedAt, DateTime UpdatedAt)
{
    public static AssessmentResponse From(Assessment a) => new(a.Id, a.WorkspaceId, a.ClientId, a.Content, a.Status == AssessmentStatus.Concluido ? "concluido" : "em_andamento", a.CreatedAt, a.UpdatedAt);
}
