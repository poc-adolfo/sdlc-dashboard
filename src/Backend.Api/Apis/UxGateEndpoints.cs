using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Backend.Api.Services;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Apis;

// gate-ux-figma.md seções 3/5: confirmação humana da sugestão de UX do Analista, e a geração dos
// mockups em SVG (perfil Hermes `ux`) depois de confirmado tem_tarefas_design = true.
public static class UxGateEndpoints
{
    public static IEndpointRouteBuilder MapUxGateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/workspaces/{id:long}/pipeline-instances/{pipelineId:long}/ux-gate/decision", GetDecision);
        app.MapPost("/workspaces/{id:long}/pipeline-instances/{pipelineId:long}/ux-gate/decision", Decide);
        app.MapPost("/workspaces/{id:long}/pipeline-instances/{pipelineId:long}/ux-gate", GenerateMockups);
        app.MapGet("/workspaces/{id:long}/pipeline-instances/{pipelineId:long}/ux-gate/{requestId}", GetMockupJob);
        app.MapGet("/workspaces/{id:long}/pipeline-instances/{pipelineId:long}/ux-gate/mockups/{mockupId:long}/content", GetMockupContent);
        return app;
    }

    // Chamado por SpecUsEndpoints.Handle/SpecProjectEndpoints.SubirUs logo depois de criar a
    // PipelineInstance, só quando o Analista devolveu uma opinião (tem_tarefas_design não-nulo) -
    // fica pendente até a confirmação humana (Decide), sem bloquear a fase Requisitos.
    public static async Task RecordSuggestionAsync(AppDbContext db, long pipelineInstanceId, DorResult dor, CancellationToken ct)
    {
        if (dor.TemTarefasDesign is null) return;
        db.UxGateDecisions.Add(new UxGateDecision
        {
            PipelineInstanceId = pipelineInstanceId,
            TemTarefasDesign = dor.TemTarefasDesign,
            JustificativaDesign = dor.JustificativaDesign,
            Confirmado = false,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private static async Task<IResult> GetDecision(long id, long pipelineId, AppDbContext db, CancellationToken ct)
    {
        var pipeline = await db.PipelineInstances.SingleOrDefaultAsync(p => p.Id == pipelineId && p.WorkspaceId == id, ct);
        if (pipeline is null) return Results.NotFound();
        var decision = await db.UxGateDecisions.AsNoTracking().Include(d => d.Mockups).SingleOrDefaultAsync(d => d.PipelineInstanceId == pipelineId, ct);
        if (decision is null) return Results.Ok(new { exists = false });
        return Results.Ok(new
        {
            exists = true,
            decision = UxGateDecisionResponse.From(decision),
            mockups = decision.Mockups.Select(m => new { m.Id, m.Nome }),
        });
    }

    private static async Task<IResult> GetMockupContent(long id, long pipelineId, long mockupId, AppDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        var mockup = await db.UxMockups.AsNoTracking().Include(m => m.UxGateDecision).ThenInclude(d => d!.PipelineInstance)
            .SingleOrDefaultAsync(m => m.Id == mockupId, ct);
        if (mockup?.UxGateDecision?.PipelineInstance is null || mockup.UxGateDecision.PipelineInstanceId != pipelineId || mockup.UxGateDecision.PipelineInstance.WorkspaceId != id)
            return Results.NotFound();
        var svg = await blobs.ReadAsync(mockup.BlobPath, ct);
        return svg is null ? Results.NotFound() : Results.Text(svg, "image/svg+xml");
    }

    private static async Task<IResult> Decide(long id, long pipelineId, UxGateDecisionRequest? request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (request is null) return Results.UnprocessableEntity(new { errors = new[] { "tem_tarefas_design: is required" } });
        var pipeline = await db.PipelineInstances.SingleOrDefaultAsync(p => p.Id == pipelineId && p.WorkspaceId == id, ct);
        if (pipeline is null) return Results.NotFound();

        var decision = await db.UxGateDecisions.SingleOrDefaultAsync(d => d.PipelineInstanceId == pipelineId, ct);
        if (decision is null)
        {
            // Sem sugestão do Analista (spec sem essa avaliação, ou skill mais antiga) - o humano ainda
            // pode registrar a decisão manualmente do zero.
            decision = new UxGateDecision { PipelineInstanceId = pipelineId, CreatedAt = DateTime.UtcNow };
            db.UxGateDecisions.Add(decision);
        }

        if (decision.TemTarefasDesign.HasValue && decision.TemTarefasDesign != request.TemTarefasDesign)
            decision.MotivoSobrescrita = request.Motivo;
        decision.TemTarefasDesign = request.TemTarefasDesign;
        decision.Confirmado = true;
        decision.DecididoPor = (string)http.Items["authenticated_user"]!;
        decision.DecididoEm = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(UxGateDecisionResponse.From(decision));
    }

    private static async Task<IResult> GenerateMockups(long id, long pipelineId, UxGateGenerateRequest? request, AppDbContext db, IHttpClientFactory clients, IConfiguration configuration, AsyncJobStore<UxGateJobResult> jobStore, IBlobStore blobs, IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("UxGateEndpoints");
        if (string.IsNullOrWhiteSpace(request?.SpecContent)) return Results.UnprocessableEntity(new { errors = new[] { "spec_content: is required" } });

        var pipeline = await db.PipelineInstances.SingleOrDefaultAsync(p => p.Id == pipelineId && p.WorkspaceId == id, ct);
        if (pipeline is null) return Results.NotFound();
        var decision = await db.UxGateDecisions.SingleOrDefaultAsync(d => d.PipelineInstanceId == pipelineId, ct);
        if (decision is null || !decision.Confirmado || decision.TemTarefasDesign != true)
            return Results.UnprocessableEntity(new { errors = new[] { "ux gate decision must be confirmed with tem_tarefas_design = true before generating mockups" } });

        var baseUrl = configuration["Ux:ApiServerBaseUrl"];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl) || parsedBaseUrl.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogError("Ux:ApiServerBaseUrl must be an absolute https URL");
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        var allowedHost = configuration["Ux:AllowedHost"];
        if (string.IsNullOrWhiteSpace(allowedHost) || !string.Equals(parsedBaseUrl.Host, allowedHost, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("Ux:ApiServerBaseUrl host {Host} does not match the configured Ux:AllowedHost", parsedBaseUrl.Host);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        // Alternativa de design system selecionada pro workspace (seção 4.2), se houver - contexto extra
        // pro perfil `ux` manter coerência visual entre specs diferentes do mesmo cliente.
        var selectedDesignSystem = await db.DesignSystemProposals.AsNoTracking()
            .Where(p => p.WorkspaceId == id && p.Selecionado)
            .FirstOrDefaultAsync(ct);

        var key = configuration["Ux:ApiServerApiKey"];
        var httpClient = clients.CreateClient("Ux");
        var jobId = jobStore.Create();

        // scopeFactory (não `db`, escopo por-requisição) porque este job sobrevive ao fim desta
        // requisição 202 - mesmo motivo de DesignSystemEndpoints.RunExploreJobAsync.
        _ = RunMockupJobAsync(jobId, decision.Id, baseUrl!, key, request.SpecContent, decision.JustificativaDesign, selectedDesignSystem, httpClient, scopeFactory, blobs, jobStore, logger);

        return Results.Json(new { requestId = jobId }, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task RunMockupJobAsync(string jobId, long uxGateDecisionId, string baseUrl, string? key, string specContent, string? justificativaDesign, DesignSystemProposal? designSystem, HttpClient httpClient, IServiceScopeFactory scopeFactory, IBlobStore blobs, AsyncJobStore<UxGateJobResult> jobStore, ILogger logger)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var messages = new List<object>();
            if (designSystem is not null)
            {
                messages.Add(new
                {
                    role = "system",
                    content = $"Design system selecionado para este workspace: {designSystem.Nome}. Paleta: {designSystem.PaletaJson}. Tipografia: {designSystem.Tipografia}. Estilo: {designSystem.Estilo}. Use como referência visual para os mockups.",
                });
            }
            messages.Add(new
            {
                role = "user",
                content = "Spec completa:\n\n" + specContent
                    + (string.IsNullOrWhiteSpace(justificativaDesign) ? "" : "\n\nMotivo do gate de UX: " + justificativaDesign)
                    + "\n\nProponha as telas necessárias e gere um SVG por tela, cada um em um bloco ```svg-mockup:<nome-da-tela> separado.",
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "v1/chat/completions"))
            {
                Content = JsonContent.Create(new { model = "ux", messages })
            };
            if (!string.IsNullOrWhiteSpace(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await httpClient.SendAsync(req, CancellationToken.None);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
            var reply = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (reply is null) { jobStore.Fail(jobId); return; }

            var screens = ParseScreens(reply);
            if (screens.Count == 0) { jobStore.Fail(jobId); return; }

            var now = DateTime.UtcNow;
            var mockups = new List<UxMockup>();
            foreach (var (nome, svg) in screens)
            {
                var blobPath = $"ux-mockups/{uxGateDecisionId}/{Slug(nome)}.svg";
                await blobs.WriteAsync(blobPath, svg, CancellationToken.None);
                mockups.Add(new UxMockup { UxGateDecisionId = uxGateDecisionId, Nome = nome, BlobPath = blobPath, CreatedAt = now });
            }
            db.UxMockups.AddRange(mockups);
            await db.SaveChangesAsync(CancellationToken.None);

            jobStore.Complete(jobId, new UxGateJobResult(mockups.Select(m => new UxMockupResponse(m.Id, m.Nome)).ToList()));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or UriFormatException)
        {
            logger.LogWarning(ex, "Ux mockup generation failed");
            jobStore.Fail(jobId);
        }
    }

    // O rótulo "svg-mockup:<nome>" deveria vir cercado por ```, mas nem sempre vem (mesmo achado de
    // formato de DesignSystemEndpoints.ParseOptions) - o padrão principal exige as cercas; o de reserva
    // aceita o rótulo sozinho seguido do elemento <svg>...</svg>, sem depender de cerca nenhuma.
    internal static List<(string Nome, string Svg)> ParseScreens(string text)
    {
        var result = new List<(string, string)>();
        foreach (Match m in Regex.Matches(text, @"```svg-mockup:(?<nome>[^\r\n`]+)\s*\r?\n(?<svg>.*?)```", RegexOptions.Singleline))
        {
            var nome = m.Groups["nome"].Value.Trim();
            var svg = m.Groups["svg"].Value.Trim();
            if (nome.Length > 0 && svg.Length > 0) result.Add((nome, svg));
        }
        if (result.Count > 0) return result;

        foreach (Match m in Regex.Matches(text, @"svg-mockup:(?<nome>[^\r\n`]+)\s*\r?\n`{0,3}\s*(?<svg><svg\b.*?</svg>)", RegexOptions.Singleline))
        {
            var nome = m.Groups["nome"].Value.Trim();
            var svg = m.Groups["svg"].Value.Trim();
            if (nome.Length > 0 && svg.Length > 0) result.Add((nome, svg));
        }
        return result;
    }

    private static string Slug(string nome) => Regex.Replace(nome.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    private static IResult GetMockupJob(long id, long pipelineId, string requestId, AsyncJobStore<UxGateJobResult> jobStore)
    {
        var job = jobStore.Get(requestId);
        if (job is null) return Results.NotFound();
        return job.Status switch
        {
            AsyncJobStatus.Done => Results.Ok(new { status = "done", mockups = job.Result!.Mockups }),
            AsyncJobStatus.Error => Results.Ok(new { status = "error" }),
            _ => Results.Ok(new { status = "pending" }),
        };
    }
}

public sealed record UxGateDecisionRequest([property: JsonPropertyName("tem_tarefas_design")] bool TemTarefasDesign, [property: JsonPropertyName("motivo")] string? Motivo = null);
public sealed record UxGateGenerateRequest([property: JsonPropertyName("spec_content")] string? SpecContent);
public sealed record UxGateJobResult(List<UxMockupResponse> Mockups);
public sealed record UxMockupResponse(long Id, string Nome);
public sealed record UxGateDecisionResponse(long Id, bool? TemTarefasDesign, string? JustificativaDesign, bool Confirmado, string? MotivoSobrescrita, string? DecididoPor, DateTime? DecididoEm)
{
    public static UxGateDecisionResponse From(UxGateDecision d) => new(d.Id, d.TemTarefasDesign, d.JustificativaDesign, d.Confirmado, d.MotivoSobrescrita, d.DecididoPor, d.DecididoEm);
}
