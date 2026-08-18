using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Api.Services;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Apis;

// gate-ux-figma.md seção 4.2: trigger independente do gate por-spec, disparado a partir do assessment.
// Chama o perfil Hermes `ux` só com o Assessment.Content (sem spec nenhuma), pedindo 3 alternativas de
// design system; o humano seleciona uma, ou pede pra renovar as 3 (novo job, mesmo endpoint).
public static class DesignSystemEndpoints
{
    public static IEndpointRouteBuilder MapDesignSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/workspaces/{id:long}/design-system/explore", Explore);
        app.MapGet("/workspaces/{id:long}/design-system/explore/{requestId}", GetExploreJob);
        app.MapGet("/workspaces/{id:long}/design-system/proposals", ListProposals);
        app.MapPost("/workspaces/{id:long}/design-system/proposals/{proposalId:long}/select", Select);
        return app;
    }

    private static async Task<IResult> Explore(long id, AppDbContext db, IHttpClientFactory clients, IConfiguration configuration, AsyncJobStore<DesignSystemJobResult> jobStore, IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("DesignSystemEndpoints");
        var assessment = await db.Assessments.AsNoTracking()
            .Where(a => a.WorkspaceId == id)
            .OrderByDescending(a => a.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (assessment is null) return Results.UnprocessableEntity(new { errors = new[] { "workspace has no assessment yet" } });

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

        // "Gerar novamente" (seção 4.2) precisa produzir alternativas de verdade diferentes das já
        // propostas - sem isso, o modelo tende a repetir a mesma resposta palavra por palavra para o
        // mesmo assessment, e o operador vê "gerar novamente" como se não tivesse feito nada.
        var previous = await db.DesignSystemProposals.AsNoTracking().Where(p => p.WorkspaceId == id).ToListAsync(ct);

        var key = configuration["Ux:ApiServerApiKey"];
        var httpClient = clients.CreateClient("Ux");
        var jobId = jobStore.Create();

        // CancellationToken.None de propósito, mesmo motivo do chat de specs: o job sobrevive ao fim
        // desta requisição 202 - e por isso não pode reusar `db` (escopo por-requisição, descartado
        // assim que este handler retorna); scopeFactory cria um AppDbContext próprio, com seu próprio
        // ciclo de vida, para a task em background usar.
        _ = RunExploreJobAsync(jobId, id, baseUrl!, key, assessment.Content, previous, httpClient, scopeFactory, jobStore, logger);

        return Results.Json(new { requestId = jobId }, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task RunExploreJobAsync(string jobId, long workspaceId, string baseUrl, string? key, string assessmentContent, List<DesignSystemProposal> previous, HttpClient httpClient, IServiceScopeFactory scopeFactory, AsyncJobStore<DesignSystemJobResult> jobStore, ILogger logger)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var userPrompt = "Proponha exatamente 3 alternativas de design system para este cliente, cada uma em um bloco ```design-system-option separado.";
            if (previous.Count > 0)
            {
                userPrompt += "\n\nJá foram propostas antes (o operador pediu para gerar de novo - as 3 novas alternativas "
                    + "precisam ser genuinamente diferentes destas em nome, paleta, tipografia e estilo, não repetições "
                    + "com palavras trocadas):\n"
                    + string.Join("\n", previous.Select(p => $"- {p.Nome}: paleta {p.PaletaJson}, tipografia {p.Tipografia}, estilo {p.Estilo}"));
            }
            var messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "Assessment deste workspace (linha de negocio, stack, arquiteturas, constraints):\n\n" + assessmentContent,
                },
                new
                {
                    role = "user",
                    content = userPrompt,
                },
            };
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

            var options = ParseOptions(reply);
            if (options.Count == 0) { jobStore.Fail(jobId); return; }

            // "Gerar novamente" substitui as propostas pendentes por uma rodada nova - a alternativa já
            // selecionada (se houver) fica intocada até uma nova seleção ser feita (gate-ux-figma.md
            // seção 4.2), então só as não-selecionadas da rodada anterior são removidas aqui.
            var stale = await db.DesignSystemProposals.Where(p => p.WorkspaceId == workspaceId && !p.Selecionado).ToListAsync(CancellationToken.None);
            db.DesignSystemProposals.RemoveRange(stale);

            var now = DateTime.UtcNow;
            var proposals = options.Select(o => new DesignSystemProposal
            {
                WorkspaceId = workspaceId,
                Nome = o.Nome,
                PaletaJson = JsonSerializer.Serialize(o.Paleta),
                Tipografia = o.Tipografia,
                Estilo = o.Estilo,
                Justificativa = o.Justificativa,
                CreatedAt = now,
            }).ToList();
            db.DesignSystemProposals.AddRange(proposals);
            await db.SaveChangesAsync(CancellationToken.None);

            jobStore.Complete(jobId, new DesignSystemJobResult(proposals.Select(DesignSystemProposalResponse.From).ToList()));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or UriFormatException)
        {
            logger.LogWarning(ex, "Ux design-system explore failed");
            jobStore.Fail(jobId);
        }
    }

    // Cada alternativa deveria vir num bloco ```design-system-option ... ``` cercando um JSON (mesma
    // convenção de bloco final estruturado usada em spec-final/dor_atendido) - mas o modelo às vezes
    // omite as cercas (confirmado em teste real contra o perfil `ux`, mesmo achado de formato já
    // registrado em piloto-perfis-analista-arquiteto.md seção 6.2 pro dor_atendido) - por isso o parse
    // não depende delas: varre o texto inteiro por objetos JSON balanceados e valida pelo schema
    // esperado, cercados ou não.
    internal static List<DesignSystemOption> ParseOptions(string text)
    {
        var result = new List<DesignSystemOption>();
        foreach (var json in ExtractJsonObjects(text))
        {
            try
            {
                using var d = JsonDocument.Parse(json);
                var r = d.RootElement;
                var nome = r.TryGetProperty("nome", out var n) ? n.GetString() : null;
                var tipografia = r.TryGetProperty("tipografia", out var t) ? t.GetString() : null;
                var estilo = r.TryGetProperty("estilo", out var e) ? e.GetString() : null;
                var justificativa = r.TryGetProperty("justificativa", out var j) ? j.GetString() : null;
                if (nome is null || tipografia is null || estilo is null || justificativa is null) continue;
                var paleta = r.TryGetProperty("paleta", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : new List<string>();
                result.Add(new DesignSystemOption(nome, paleta, tipografia, estilo, justificativa));
            }
            catch (JsonException) { }
        }
        return result;
    }

    // Extrai todos os objetos {..} de nível superior de um texto, por contagem de chaves balanceadas
    // (ignora chaves dentro de strings) - candidatos inválidos simplesmente falham o JsonDocument.Parse
    // do chamador e são descartados, então um falso positivo aqui é inofensivo.
    internal static List<string> ExtractJsonObjects(string text)
    {
        var result = new List<string>();
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') { if (depth == 0) start = i; depth++; }
            else if (c == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0) { result.Add(text[start..(i + 1)]); start = -1; }
            }
        }
        return result;
    }

    private static IResult GetExploreJob(long id, string requestId, AsyncJobStore<DesignSystemJobResult> jobStore)
    {
        var job = jobStore.Get(requestId);
        if (job is null) return Results.NotFound();
        return job.Status switch
        {
            AsyncJobStatus.Done => Results.Ok(new { status = "done", proposals = job.Result!.Proposals }),
            AsyncJobStatus.Error => Results.Ok(new { status = "error" }),
            _ => Results.Ok(new { status = "pending" }),
        };
    }

    private static async Task<IResult> ListProposals(long id, AppDbContext db, CancellationToken ct)
    {
        var proposals = await db.DesignSystemProposals.AsNoTracking()
            .Where(p => p.WorkspaceId == id)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(proposals.Select(DesignSystemProposalResponse.From));
    }

    // "Selecionar" marca uma proposta e desmarca qualquer outra já selecionada para o mesmo workspace -
    // no máximo uma seleção ativa por vez (gate-ux-figma.md seção 4.2).
    private static async Task<IResult> Select(long id, long proposalId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var proposal = await db.DesignSystemProposals.SingleOrDefaultAsync(p => p.Id == proposalId, ct);
        if (proposal is null || proposal.WorkspaceId != id) return Results.NotFound();

        var previouslySelected = await db.DesignSystemProposals.Where(p => p.WorkspaceId == id && p.Selecionado).ToListAsync(ct);
        foreach (var p in previouslySelected) p.Selecionado = false;

        proposal.Selecionado = true;
        proposal.SelecionadoPor = (string)http.Items["authenticated_user"]!;
        proposal.SelecionadoEm = DateTime.UtcNow;

        // Selecionar precisa ficar salvo no assessment, não só na tabela de propostas - o operador pediu
        // isso explicitamente depois de perceber que a seleção não se refletia em lugar nenhum visível
        // do assessment. O assessment mais recente do workspace (concluído ou não) é sempre o certo.
        var assessment = await db.Assessments.Where(a => a.WorkspaceId == id).OrderByDescending(a => a.UpdatedAt).FirstOrDefaultAsync(ct);
        if (assessment is not null) assessment.SelectedDesignSystemProposalId = proposal.Id;

        await db.SaveChangesAsync(ct);
        return Results.Ok(DesignSystemProposalResponse.From(proposal));
    }
}

internal sealed record DesignSystemOption(string Nome, List<string> Paleta, string Tipografia, string Estilo, string Justificativa);
public sealed record DesignSystemJobResult(List<DesignSystemProposalResponse> Proposals);
public sealed record DesignSystemProposalResponse(long Id, string Nome, List<string> Paleta, string Tipografia, string Estilo, string Justificativa, bool Selecionado, DateTime CreatedAt)
{
    public static DesignSystemProposalResponse From(DesignSystemProposal p) =>
        new(p.Id, p.Nome, JsonSerializer.Deserialize<List<string>>(p.PaletaJson) ?? new(), p.Tipografia, p.Estilo, p.Justificativa, p.Selecionado, p.CreatedAt);
}
