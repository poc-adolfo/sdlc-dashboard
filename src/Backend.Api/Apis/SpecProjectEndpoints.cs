using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Backend.Api.Services;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Apis;

/// <summary>
/// Specs storage per seção 5.2 (atualização 2026-08-09): specs now live in blob storage under
/// specs/{client_id}/{projeto}/{fileName}, not in the workspace's Git repo - "projeto" has no row in the
/// database, it is only a path prefix inside the blob container (via IBlobStore, the same abstraction
/// AssessmentEndpoints uses for its markdown export). This coexists with the older Git-based flow
/// (SpecListingEndpoints/SpecUsEndpoints) rather than replacing it outright; "Subir US" here reuses that
/// flow's DoR-gate-check/extract/publish pipeline, just sourcing spec content from a blob instead of a
/// repo file, and never touches the `spec` index table that only the Git-based listing maintains.
/// </summary>
public static class SpecProjectEndpoints
{
    private const int MaxSegmentLength = 100;
    private const string ProjectMarker = ".project";

    public static IEndpointRouteBuilder MapSpecProjectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/workspaces/{id:long}/spec-projects", ListProjects);
        app.MapPost("/workspaces/{id:long}/spec-projects", CreateProject);
        app.MapGet("/workspaces/{id:long}/spec-projects/{projeto}/specs", ListSpecs);
        app.MapGet("/workspaces/{id:long}/spec-projects/{projeto}/specs/{fileName}", GetSpec);
        app.MapPut("/workspaces/{id:long}/spec-projects/{projeto}/specs/{fileName}", PutSpec);
        app.MapPost("/workspaces/{id:long}/spec-projects/{projeto}/specs/{fileName}/subir-us", SubirUs);
        app.MapPost("/workspaces/{id:long}/spec-projects/{projeto}/specs/{fileName}/chat", Chat);
        app.MapGet("/workspaces/{id:long}/spec-projects/{projeto}/specs/{fileName}/chat/{requestId}", GetChatJob);
        return app;
    }

    // "projeto"/"fileName" become blob path segments - a literal "/" or ".." in either would let a
    // request read/write outside its own client's prefix, so every route that takes one rejects those
    // up front instead of trusting ASP.NET routing to have fully normalized them.
    private static bool IsSafeSegment(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxSegmentLength && !value.Contains('/') && !value.Contains('\\') && value != "." && value != "..";

    private static string ProjectsPrefix(long clientId) => $"specs/{clientId}/";
    private static string ProjectPrefix(long clientId, string projeto) => $"specs/{clientId}/{projeto}/";

    private static async Task<(Workspace? Workspace, IResult? Error)> LoadWorkspaceWithClient(long id, AppDbContext db, CancellationToken ct)
    {
        var workspace = await db.Workspaces.SingleOrDefaultAsync(w => w.Id == id, ct);
        if (workspace is null) return (null, Results.NotFound());
        // Workspace.ClientId is only ever set by AssessmentEndpoints.Conclude (seção 5.1/5.2) - a
        // workspace without a concluded assessment has nowhere in the blob container to keep specs.
        if (workspace.ClientId is null) return (null, Results.UnprocessableEntity(new { errors = new[] { "conclude this workspace's assessment before working with specs" } }));
        return (workspace, null);
    }

    private static async Task<IResult> ListProjects(long id, AppDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        var (workspace, error) = await LoadWorkspaceWithClient(id, db, ct);
        if (error is not null) return error;

        var prefix = ProjectsPrefix(workspace!.ClientId!.Value);
        var entries = await blobs.ListAsync(prefix, ct);
        var projects = entries
            .Select(e => e.Name[prefix.Length..])
            .Where(rest => rest.Length > 0)
            .Select(rest => rest.Split('/', 2)[0])
            .Where(name => name.Length > 0)
            .Distinct()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Results.Ok(projects);
    }

    private static async Task<IResult> CreateProject(long id, CreateProjectRequest? request, AppDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        var (workspace, error) = await LoadWorkspaceWithClient(id, db, ct);
        if (error is not null) return error;

        var name = request?.Name?.Trim();
        if (!IsSafeSegment(name)) return Results.UnprocessableEntity(new { errors = new[] { "name: is required and must not contain '/'" } });

        // A brand new project has no spec blobs to be listed by (its prefix), so ListProjects would
        // never see it without this marker - specs written into it later just live alongside it.
        await blobs.WriteAsync(ProjectPrefix(workspace!.ClientId!.Value, name!) + ProjectMarker, "", ct);
        return Results.Created($"/workspaces/{id}/spec-projects/{Uri.EscapeDataString(name!)}", new { name });
    }

    private static async Task<IResult> ListSpecs(long id, string projeto, AppDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        if (!IsSafeSegment(projeto)) return Results.NotFound();
        var (workspace, error) = await LoadWorkspaceWithClient(id, db, ct);
        if (error is not null) return error;

        var prefix = ProjectPrefix(workspace!.ClientId!.Value, projeto);
        var entries = await blobs.ListAsync(prefix, ct);
        var items = new List<SpecFileItem>();
        foreach (var entry in entries)
        {
            var fileName = entry.Name[prefix.Length..];
            if (fileName.Length == 0 || fileName == ProjectMarker || fileName.Contains('/')) continue;
            var content = await blobs.ReadAsync(entry.Name, ct);
            if (content is null) continue; // best-effort: one unreadable blob must not fail the whole listing
            items.Add(new SpecFileItem(fileName, SpecListingEndpoints.ParseTitle(content, fileName), SpecListingEndpoints.ParseStatus(content), 1, entry.LastModified));
        }
        items.Sort((a, b) => string.CompareOrdinal(a.FileName, b.FileName));
        return Results.Ok(items);
    }

    private static async Task<IResult> GetSpec(long id, string projeto, string fileName, AppDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        if (!IsSafeSegment(projeto) || !IsSafeSegment(fileName)) return Results.NotFound();
        var (workspace, error) = await LoadWorkspaceWithClient(id, db, ct);
        if (error is not null) return error;

        var content = await blobs.ReadAsync(ProjectPrefix(workspace!.ClientId!.Value, projeto) + fileName, ct);
        return content is null ? Results.NotFound() : Results.Text(content, "text/plain");
    }

    private static async Task<IResult> PutSpec(long id, string projeto, string fileName, PutSpecRequest? request, AppDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        if (!IsSafeSegment(projeto) || !IsSafeSegment(fileName)) return Results.NotFound();
        if (request?.Content is null) return Results.UnprocessableEntity(new { errors = new[] { "content: is required" } });
        var (workspace, error) = await LoadWorkspaceWithClient(id, db, ct);
        if (error is not null) return error;

        await blobs.WriteAsync(ProjectPrefix(workspace!.ClientId!.Value, projeto) + fileName, request.Content, ct);
        return Results.Ok(new { saved = true });
    }

    private static async Task<IResult> SubirUs(long id, string projeto, string fileName, AppDbContext db, IBlobStore blobs, PlatformContentClient platform, AnalystDorGate gate, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!IsSafeSegment(projeto) || !IsSafeSegment(fileName)) return Results.NotFound();
        var (workspace, error) = await LoadWorkspaceWithClient(id, db, ct);
        if (error is not null) return error;

        var fetched = await blobs.ReadAsync(ProjectPrefix(workspace!.ClientId!.Value, projeto) + fileName, ct);
        if (fetched is null) return Results.NotFound();

        var dor = await gate.CheckAsync(fetched, ct);
        if (dor is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
        if (!dor.Attended) return Results.Ok(new { dor_atendido = false, pendencias = dor.Pending });

        var body = SpecUsEndpoints.Extract(fetched, loggerFactory.CreateLogger("SpecProjectEndpoints"));
        var title = Regex.Match(fetched, @"(?m)^#\s+(.+?)\s*$").Groups[1].Value.Trim();
        if (title.Length == 0) title = "Sem titulo";

        var external = await SpecUsEndpoints.Publish(workspace, title, body, platform, ct);
        if (external is null) return Results.StatusCode(StatusCodes.Status502BadGateway);

        // No `spec` index row backs a blob-stored spec (that table only indexes the older Git-based
        // flow, SpecListingEndpoints) - SpecId stays null here, same as any pipeline started without one.
        db.SpecPublications.Add(new SpecPublication { WorkspaceId = id, SpecId = null, ExternalRef = external, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);

        var pipeline = new PipelineInstance { WorkspaceId = id, SpecId = null, FaseAtual = PipelinePhase.Requisitos, GateStatus = GateStatus.Approved, ExternalRef = external, CreatedAt = DateTime.UtcNow };
        db.PipelineInstances.Add(pipeline);
        await db.SaveChangesAsync(ct);
        return Results.Json(new { pipeline_instance = new PipelineInstanceResponse(pipeline.Id, pipeline.WorkspaceId, pipeline.SpecId, pipeline.FaseAtual.ToString(), pipeline.GateStatus.ToString(), pipeline.ExternalRef, pipeline.PrRef, pipeline.CreatedAt) }, statusCode: 201);
    }

    // Talks to a *different* Hermes-hosted skill/profile than AnalystDorGate (Specs:ApiServerBaseUrl,
    // not Analista:*) - free-form conversation with the "specs" skill, not the DoR gate verdict the
    // Analista profile returns, so it is not routed through AnalystDorGate itself even though the wire
    // mechanism (POST {api_server}/v1/chat/completions) is identical. The HTTPS/allowed-host pinning is
    // duplicated from there rather than shared, for the same reason AzureBlobStore/S3BlobStore don't
    // share a base class: two small, independently-reviewable checks beat one shared abstraction that
    // both a spec-chat bug and an Analista-gate bug could each separately compromise.
    //
    // Async by design (não síncrono como o resto desta classe): uma resposta rica da skill pode levar
    // bem mais que o antigo timeout de 30s do HttpClient.Timeout (que era literalmente o tempo que o
    // browser ficava esperando essa própria requisição). Em vez de o operador ver "Não foi possível
    // falar com a skill" toda vez que a resposta demora, o POST só valida tudo (rápido: config, sessão,
    // workspace, blob) e devolve 202 com um requestId - a chamada de verdade ao api_server roda em
    // background (RunChatJobAsync) fora do ciclo de vida desta requisição, e o frontend faz polling em
    // GetChatJob até o job sair de "pending".
    private static async Task<IResult> Chat(long id, string projeto, string fileName, ChatRequest? request, AppDbContext db, IBlobStore blobs, IHttpClientFactory clients, IConfiguration configuration, SpecChatJobStore jobStore, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!IsSafeSegment(projeto) || !IsSafeSegment(fileName)) return Results.NotFound();
        if (request?.Messages is null || request.Messages.Count == 0) return Results.UnprocessableEntity(new { errors = new[] { "messages: is required" } });
        var (workspace, error) = await LoadWorkspaceWithClient(id, db, ct);
        if (error is not null) return error;

        var logger = loggerFactory.CreateLogger("SpecProjectEndpoints");
        var baseUrl = configuration["Specs:ApiServerBaseUrl"];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl) || parsedBaseUrl.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogError("Specs:ApiServerBaseUrl must be an absolute https URL");
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        var allowedHost = configuration["Specs:AllowedHost"];
        if (string.IsNullOrWhiteSpace(allowedHost) || !string.Equals(parsedBaseUrl.Host, allowedHost, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("Specs:ApiServerBaseUrl host {Host} does not match the configured Specs:AllowedHost", parsedBaseUrl.Host);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        // Premissas do cliente (linha de negocio, stack, arquitetura, constraints de seguranca - seção
        // 5.1) entram como uma mensagem de sistema antes do histórico da conversa, pro SOUL da skill
        // poder fundamentar sugestões no contexto real do workspace em vez de assumir tecnologias/
        // restrições genéricas. Sem assessment concluído (workspace legado ou ainda incompleto), o chat
        // segue sem esse contexto - a skill não deve travar por causa disso.
        var assessment = await db.Assessments.AsNoTracking()
            .Where(a => a.WorkspaceId == id && a.Status == AssessmentStatus.Concluido)
            .OrderByDescending(a => a.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        // Conteúdo atual da spec (se já existir algo salvo, ex: operador clicou "Atualizar spec" no
        // modal - seção 5.2/5.4) entra como contexto também, senão toda revisão pareceria um pedido pra
        // começar do zero. Uma spec nova (ainda só o template vazio) não tem nada de útil aqui, mas
        // incluir mesmo assim não atrapalha - só reduz para "nao revise nada, é so o template".
        var currentContent = await blobs.ReadAsync(ProjectPrefix(workspace!.ClientId!.Value, projeto) + fileName, ct);

        var messages = new List<object>();
        if (assessment is not null)
        {
            messages.Add(new
            {
                role = "system",
                content = "Premissas do assessment deste workspace (linha de negocio do cliente, stack, "
                    + "arquiteturas presentes, constraints de seguranca) - use como contexto para suas "
                    + "sugestoes, sem repetir o texto de volta sem necessidade:\n\n" + assessment.Content,
            });
        }
        if (!string.IsNullOrWhiteSpace(currentContent))
        {
            messages.Add(new
            {
                role = "system",
                content = "Conteudo atual salvo desta spec (o operador pode estar revisando/continuando "
                    + "algo ja existente, nao necessariamente comecando do zero) - use como ponto de "
                    + "partida quando fizer sentido:\n\n" + currentContent,
            });
        }
        messages.AddRange(request.Messages.Select(m => (object)new { role = m.Role, content = m.Content }));

        var key = configuration["Specs:ApiServerApiKey"];
        var blobPath = ProjectPrefix(workspace!.ClientId!.Value, projeto) + fileName;
        var httpClient = clients.CreateClient("Specs");
        var jobId = jobStore.Create();

        // CancellationToken.None de propósito: `ct` morre quando esta resposta 202 termina, mas o
        // trabalho de verdade só começa depois disso - precisa sobreviver ao fim do request.
        _ = RunChatJobAsync(jobId, baseUrl!, key, messages, httpClient, blobs, blobPath, jobStore, logger);

        return Results.Json(new { requestId = jobId }, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task RunChatJobAsync(string jobId, string baseUrl, string? key, List<object> messages, HttpClient httpClient, IBlobStore blobs, string blobPath, SpecChatJobStore jobStore, ILogger logger)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "v1/chat/completions"))
            {
                Content = JsonContent.Create(new { model = "specs", messages })
            };
            if (!string.IsNullOrWhiteSpace(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await httpClient.SendAsync(req, CancellationToken.None);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
            var reply = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (reply is null) { jobStore.Fail(jobId); return; }

            // Iteração termina quando o SOUL decide que a spec está pronta e responde com o bloco
            // ```spec-final ... ``` (mesmo padrão de "resposta estruturada reconhecida pelo backend" já
            // usado pro dor_atendido/pendencias do Analista) - a partir daí o backend grava no blob em
            // nome da skill (que não tem acesso a ferramentas) e o operador só vê "spec pronta" + o botão
            // de visualizar, sem precisar copiar/colar nada.
            var finalMatch = Regex.Match(reply, @"```spec-final\s*\r?\n(.*?)```", RegexOptions.Singleline);
            if (finalMatch.Success)
            {
                var finalContent = finalMatch.Groups[1].Value.Trim();
                if (finalContent.Length > 0)
                {
                    await blobs.WriteAsync(blobPath, finalContent, CancellationToken.None);
                    jobStore.Complete(jobId, "Sua spec ficou pronta.", finalized: true);
                    return;
                }
            }

            jobStore.Complete(jobId, reply, finalized: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or UriFormatException)
        {
            logger.LogWarning(ex, "Specs skill chat failed");
            jobStore.Fail(jobId);
        }
    }

    private static IResult GetChatJob(long id, string projeto, string fileName, string requestId, SpecChatJobStore jobStore)
    {
        if (!IsSafeSegment(projeto) || !IsSafeSegment(fileName)) return Results.NotFound();
        var job = jobStore.Get(requestId);
        if (job is null) return Results.NotFound();
        return job.Status switch
        {
            SpecChatJobStatus.Done => Results.Ok(new { status = "done", reply = job.Reply, finalized = job.Finalized }),
            SpecChatJobStatus.Error => Results.Ok(new { status = "error" }),
            _ => Results.Ok(new { status = "pending" }),
        };
    }
}

public sealed record SpecFileItem(string FileName, string Title, string? Status, int Version, DateTimeOffset? UpdatedAt);
public sealed record CreateProjectRequest(string? Name);
public sealed record PutSpecRequest(string? Content);
public sealed record ChatMessageDto(string Role, string Content);
public sealed record ChatRequest(List<ChatMessageDto>? Messages);
