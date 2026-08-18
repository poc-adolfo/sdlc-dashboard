using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Backend.Api.Apis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Tests;

public sealed class SpecProjectApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"spec-projects-tests-{Guid.NewGuid():N}.db");
    public RoutingFakeHandler Handler { get; } = new();
    public FakeBlobStore Blobs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Username"] = "operator",
            ["Authentication:Password"] = "secret",
            ["Authentication:SigningKey"] = "DoBqIVy5zTyTGicih2WShaYg6goTsq0lvS7XlPiHWps=",
            ["Authentication:SecureCookie"] = "false",
            ["ConnectionStrings:Default"] = $"Data Source={_databasePath}",
            ["Analista:ApiServerBaseUrl"] = "https://analista.test",
            ["Analista:AllowedHost"] = "analista.test",
            ["Specs:ApiServerBaseUrl"] = "https://analista.test",
            ["Specs:AllowedHost"] = "analista.test",
            ["GitHub:AppToken"] = "test-token",
            ["AzureDevOps:AppToken"] = "test-token",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("Platform").ConfigurePrimaryHttpMessageHandler(() => Handler);
            services.AddHttpClient("Analista").ConfigurePrimaryHttpMessageHandler(() => Handler);
            services.AddHttpClient("Specs").ConfigurePrimaryHttpMessageHandler(() => Handler);
            services.AddSingleton<Backend.Api.Services.IBlobStore>(Blobs);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            var path = _databasePath + suffix;
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }
}

public sealed class SpecProjectEndpointsTests
{
    private static HttpResponseMessage AnalistaResponse(string dorJson) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { choices = new[] { new { message = new { content = dorJson } } } })
    };

    private static async Task<HttpClient> AuthenticatedClient(SpecProjectApplicationFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Creates a workspace and concludes its assessment (so Workspace.ClientId is set) - every spec-projects route requires that.</summary>
    private static async Task<long> CreateWorkspaceWithConcludedAssessment(HttpClient client, string clientName = "Acme")
    {
        var workspace = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform" });
        workspace.EnsureSuccessStatusCode();
        var workspaceId = (await workspace.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var assessment = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/assessments", new { client_name = clientName, content = "x" });
        assessment.EnsureSuccessStatusCode();
        var assessmentId = (await assessment.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var conclude = await client.PostAsync($"/workspaces/{workspaceId}/assessments/{assessmentId}/concluir", content: null);
        conclude.EnsureSuccessStatusCode();
        return workspaceId;
    }

    [Fact]
    public async Task ListProjectsIsEmptyForAFreshlyConcludedWorkspace()
    {
        using var factory = new SpecProjectApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);

        var response = await client.GetAsync($"/workspaces/{workspaceId}/spec-projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SpecProjectsRoutesRejectAWorkspaceWhoseAssessmentWasNeverConcluded()
    {
        using var factory = new SpecProjectApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspace = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform" });
        var workspaceId = (await workspace.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var response = await client.GetAsync($"/workspaces/{workspaceId}/spec-projects");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreatingAProjectMakesItAppearInTheListingEvenWithNoSpecsYet()
    {
        using var factory = new SpecProjectApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);

        var create = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/spec-projects", new { name = "checkout" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var projects = await client.GetFromJsonAsync<string[]>($"/workspaces/{workspaceId}/spec-projects");
        Assert.Equal(new[] { "checkout" }, projects);
    }

    [Fact]
    public async Task PathTraversalInProjectNameIsRejected()
    {
        using var factory = new SpecProjectApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);

        var create = await client.PostAsJsonAsync($"/workspaces/{workspaceId}/spec-projects", new { name = "../escape" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
    }

    [Fact]
    public async Task PuttingAndGettingASpecRoundTripsItsRawContent()
    {
        using var factory = new SpecProjectApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        const string markdown = "# Checkout\n\n> Status: rascunho (2026-08-05).\n\nConteudo.\n";

        var put = await client.PutAsJsonAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md", new { content = markdown });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(markdown, await get.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetOnAMissingSpecReturnsNotFound()
    {
        using var factory = new SpecProjectApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);

        var response = await client.GetAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/missing.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListSpecsParsesTitleAndStatusFromContentAndSkipsTheProjectMarker()
    {
        using var factory = new SpecProjectApplicationFactory();
        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        const string markdown = "# Checkout flow\n\n> Status: rascunho (2026-08-05).\n\nConteudo.\n";
        await client.PutAsJsonAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md", new { content = markdown });

        var specs = await client.GetFromJsonAsync<JsonElement>($"/workspaces/{workspaceId}/spec-projects/checkout/specs");

        Assert.Equal(1, specs.GetArrayLength());
        var item = specs[0];
        Assert.Equal("foo.md", item.GetProperty("fileName").GetString());
        Assert.Equal("Checkout flow", item.GetProperty("title").GetString());
        Assert.Equal("rascunho", item.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SubirUsPublishesToGitHubFromBlobContentWithoutRequiringASpecIndexRow()
    {
        using var factory = new SpecProjectApplicationFactory();
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse("{\"dor_atendido\": true, \"pendencias\": []}"));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { number = 42 }) });

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        const string markdown = "# Checkout\n\n## User Story\n**Como** operador, **quero** publicar.\n\n## Criterios de aceite\n- [ ] ok\n\n## WBS - Plano de implementacao\n1. Endpoint\n";
        await client.PutAsJsonAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md", new { content = markdown });

        var response = await client.PostAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("42", body.GetProperty("pipeline_instance").GetProperty("externalRef").GetString());
        var specIdKind = body.GetProperty("pipeline_instance").GetProperty("specId").ValueKind;
        Assert.Equal(JsonValueKind.Null, specIdKind);

        var issueCall = Assert.Single(factory.Handler.Captured, c => c.Uri.AbsolutePath.EndsWith("/issues"));
        var issuePayload = JsonDocument.Parse(issueCall.Body!).RootElement;
        Assert.Equal("US: Checkout", issuePayload.GetProperty("title").GetString());
    }

    [Fact]
    public async Task SubirUsReturnsPendenciasWithoutPublishingWhenDorBlocks()
    {
        using var factory = new SpecProjectApplicationFactory();
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse("{\"dor_atendido\": false, \"pendencias\": [\"falta criterio X\"]}"));
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => throw new InvalidOperationException("must not publish when DoR blocks"));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        await client.PutAsJsonAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md", new { content = "# Checkout\n" });

        var response = await client.PostAsync($"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md/subir-us", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("dor_atendido").GetBoolean());
        Assert.Equal("falta criterio X", body.GetProperty("pendencias")[0].GetString());
    }

    [Fact]
    public async Task ChatCallsTheSpecsSkillAndReturnsItsReply()
    {
        using var factory = new SpecProjectApplicationFactory();
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse("Aqui esta uma sugestao."));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        var basePath = $"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md";
        await client.PutAsJsonAsync(basePath, new { content = "# Checkout\n" });

        var response = await client.PostAsJsonAsync($"{basePath}/chat", new { messages = new[] { new { role = "user", content = "Pode revisar?" } } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var requestId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestId").GetString();

        var job = await PollChatJob(client, basePath, requestId!);

        Assert.Equal("done", job.GetProperty("status").GetString());
        Assert.Equal("Aqui esta uma sugestao.", job.GetProperty("reply").GetString());

        var call = Assert.Single(factory.Handler.Captured, c => c.Uri.Host == "analista.test");
        var payload = JsonDocument.Parse(call.Body!).RootElement;
        Assert.Equal("specs", payload.GetProperty("model").GetString());
        var messages = payload.GetProperty("messages");
        Assert.Equal("Pode revisar?", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
    }

    // Uma spec grande demais mandada como uma unica mensagem de sistema estreita o espaco que sobra pro
    // modelo remoto terminar a propria resposta sem cortar (a causa raiz por tras de LooksTruncated) -
    // ChunkedSystemMessage existe pra evitar isso, quebrando em varias mensagens de sistema menores.
    [Fact]
    public async Task ChatSplitsALargeCurrentSpecIntoMultipleSystemMessages()
    {
        using var factory = new SpecProjectApplicationFactory();
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse("ok"));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        var basePath = $"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md";
        var big = "# Checkout\n\n" + string.Concat(Enumerable.Repeat("Linha de conteudo bem detalhada da spec.\n", 300));
        await client.PutAsJsonAsync(basePath, new { content = big });

        var response = await client.PostAsJsonAsync($"{basePath}/chat", new { messages = new[] { new { role = "user", content = "Continua" } } });
        var requestId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestId").GetString();
        await PollChatJob(client, basePath, requestId!);

        var call = Assert.Single(factory.Handler.Captured, c => c.Uri.Host == "analista.test");
        var payload = JsonDocument.Parse(call.Body!).RootElement;
        var messages = payload.GetProperty("messages").EnumerateArray().ToList();

        // A mensagem de sistema com o conteudo atual (unica cujo texto contem "Linha de conteudo") deve
        // ter sido quebrada em mais de uma mensagem, cada uma abaixo do limite de tamanho.
        var specMessages = messages.Where(m => m.GetProperty("content").GetString()!.Contains("Linha de conteudo")).ToList();
        Assert.True(specMessages.Count > 1, "conteudo grande deveria ter sido dividido em varias mensagens");
        Assert.All(specMessages, m => Assert.True(m.GetProperty("content").GetString()!.Length <= 4000));
        // A ultima mensagem (a pergunta do operador) continua intacta, no fim da lista.
        Assert.Equal("Continua", messages[^1].GetProperty("content").GetString());
    }

    // 2026-08-18: o chunking de saida (teste acima) nao bastou sozinho - o modelo remoto ainda cortava a
    // propria geracao no meio da frase mesmo com o payload de entrada menor. Pedir pra "regenerar tudo de
    // novo" tambem nao ajudou (observado ao vivo: 3 tentativas, cortando em pontos diferentes a cada vez,
    // nunca terminando) - RunChatJobAsync agora pede continuacao de verdade e cola o que volta no que ja
    // foi acumulado.
    [Fact]
    public async Task ChatAutomaticallyAsksTheSkillToContinueATruncatedReplyAndAppendsTheContinuation()
    {
        using var factory = new SpecProjectApplicationFactory();
        var callCount = 0;
        const string firstChunk = "# Checkout\n\n### 2.1\n\nResultado registrado com PowerShell:";
        const string continuation = "Processo executado com sucesso, sem erros reportados.";
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ =>
        {
            callCount++;
            // A 2a chamada devolve so o texto que falta, sem reabrir ```spec-final - o caso mais comum,
            // ja que o pedido de continuacao pede explicitamente por isso.
            var content = callCount == 1 ? $"Quase pronta.\n```spec-final\n{firstChunk}\n```" : continuation;
            return AnalistaResponse(content);
        });

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        var basePath = $"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md";
        await client.PutAsJsonAsync(basePath, new { content = "# Checkout\n" });

        var post = await client.PostAsJsonAsync($"{basePath}/chat", new { messages = new[] { new { role = "user", content = "Finaliza" } } });
        var requestId = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestId").GetString();

        var job = await PollChatJob(client, basePath, requestId!);

        Assert.Equal(2, callCount);
        Assert.True(job.GetProperty("finalized").GetBoolean());
        // Colado, nao substituido: o pedaco inicial continua no conteudo final, com a continuacao logo
        // depois.
        Assert.Contains(factory.Blobs.Written, w => w.Content == $"{firstChunk}\n{continuation}");

        // A segunda chamada deve carregar a resposta cortada de volta como turno do assistente, seguida
        // de um pedido explicito de continuacao - nao um pedido do zero, do contrario o contexto da
        // primeira tentativa se perderia.
        var secondCall = factory.Handler.Captured.Where(c => c.Uri.Host == "analista.test").ElementAt(1);
        var secondPayload = JsonDocument.Parse(secondCall.Body!).RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("assistant", secondPayload[^2].GetProperty("role").GetString());
        Assert.Contains("Continue exatamente de onde parou", secondPayload[^1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ChatGivesUpAfterExhaustingRegenerationAttemptsWithoutSavingAnything()
    {
        using var factory = new SpecProjectApplicationFactory();
        var callCount = 0;
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ =>
        {
            callCount++;
            return AnalistaResponse("Quase pronta.\n```spec-final\nResultado registrado com PowerShell:\n```");
        });

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        var basePath = $"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md";
        await client.PutAsJsonAsync(basePath, new { content = "# Checkout\n" });

        var post = await client.PostAsJsonAsync($"{basePath}/chat", new { messages = new[] { new { role = "user", content = "Finaliza" } } });
        var requestId = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestId").GetString();

        var job = await PollChatJob(client, basePath, requestId!);

        Assert.Equal(3, callCount); // tentativa original + 2 pedidos de continuacao (MaxContinuationAttempts)
        Assert.False(job.GetProperty("finalized").GetBoolean());
        Assert.Contains("mesmo depois de pedir pra continuar", job.GetProperty("reply").GetString());
        var saved = await client.GetAsync(basePath);
        Assert.Equal("# Checkout\n", await saved.Content.ReadAsStringAsync());
    }

    [Fact]
    public void AppendContinuationJoinsPlainContinuationWithALineBreak()
    {
        var result = SpecProjectEndpoints.AppendContinuation("Linha 1:", "Linha 2 completa.");
        Assert.Equal("Linha 1:\nLinha 2 completa.", result);
    }

    [Fact]
    public void AppendContinuationTrimsOverlapWhenTheModelRepeatsTheTailOfWhatWasAlreadyWritten()
    {
        var result = SpecProjectEndpoints.AppendContinuation(
            "O resultado foi registrado com PowerShell:",
            "registrado com PowerShell:\nNenhum arquivo encontrado.");
        Assert.Equal("O resultado foi registrado com PowerShell:\nNenhum arquivo encontrado.", result);
    }

    [Fact]
    public void AppendContinuationReturnsTheOriginalWhenTheContinuationIsPureRepetition()
    {
        const string accumulated = "O resultado foi registrado com PowerShell:";
        var result = SpecProjectEndpoints.AppendContinuation(accumulated, accumulated);
        Assert.Equal(accumulated, result);
    }

    [Fact]
    public void ChunkedSystemMessageReturnsASingleMessageWhenContentFitsWithinTheLimit()
    {
        var result = SpecProjectEndpoints.ChunkedSystemMessage("Intro:\n\n", "conteudo curto").ToList();

        var message = Assert.Single(result);
        Assert.Equal("Intro:\n\nconteudo curto", ((dynamic)message).content);
    }

    [Fact]
    public void ChunkedSystemMessageSplitsOnLineBreaksAndPreservesFullContent()
    {
        var lines = Enumerable.Range(1, 400).Select(i => $"Linha {i} com algum texto de exemplo.");
        var content = string.Join('\n', lines);

        var chunks = SpecProjectEndpoints.ChunkedSystemMessage("Intro:\n\n", content)
            .Select(m => (string)((dynamic)m).content)
            .ToList();

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 4000));
        // Concatenar os pedacos de volta (removendo os cabecalhos "(parte X/Y)") deve reconstituir o
        // texto original sem perder ou duplicar nada.
        var rebuilt = string.Concat(chunks.Select(c => Regex.Replace(c, @"^\(parte \d+/\d+\)\n\n", "")));
        Assert.Equal("Intro:\n\n" + content, rebuilt);
    }

    private static async Task<JsonElement> PollChatJob(HttpClient client, string basePath, string requestId)
    {
        for (var i = 0; i < 50; i++)
        {
            var response = await client.GetAsync($"{basePath}/chat/{requestId}");
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (body.GetProperty("status").GetString() != "pending") return body;
            await Task.Delay(20);
        }
        throw new TimeoutException("chat job never left pending status");
    }

    // 2026-08-18: o Analista relatou uma spec recebida com a secao 2.1 cortada no meio da frase - a
    // causa raiz era o proprio ```spec-final da skill "specs" tendo parado nesse ponto (limite de geracao
    // do modelo remoto), mas com a cerca ainda fechada normalmente, o que passava batido pela extracao do
    // bloco. LooksTruncated (chamado de dentro de RunChatJobAsync) existe pra barrar esse caso antes da
    // gravacao no blob.
    [Fact]
    public async Task ChatDoesNotSaveASpecFinalBlockThatLooksCutOffMidSentence()
    {
        using var factory = new SpecProjectApplicationFactory();
        const string truncated = "# Checkout\n\n### 2.1 Evidencias da investigacao\n\nResultado registrado na investigacao realizada com PowerShell:";
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse($"Aqui esta a spec.\n```spec-final\n{truncated}\n```"));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        var basePath = $"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md";
        await client.PutAsJsonAsync(basePath, new { content = "# Checkout\n" });

        var post = await client.PostAsJsonAsync($"{basePath}/chat", new { messages = new[] { new { role = "user", content = "Finaliza" } } });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var requestId = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestId").GetString();

        var job = await PollChatJob(client, basePath, requestId!);

        Assert.Equal("done", job.GetProperty("status").GetString());
        Assert.False(job.GetProperty("finalized").GetBoolean());
        var stillOriginal = await client.GetAsync(basePath);
        Assert.Equal("# Checkout\n", await stillOriginal.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChatSavesASpecFinalBlockThatLooksComplete()
    {
        using var factory = new SpecProjectApplicationFactory();
        const string complete = "# Checkout\n\n### 2.1 Evidencias da investigacao\n\nA pasta esta vazia.";
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => AnalistaResponse($"Pronta.\n```spec-final\n{complete}\n```"));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateWorkspaceWithConcludedAssessment(client);
        var basePath = $"/workspaces/{workspaceId}/spec-projects/checkout/specs/foo.md";
        await client.PutAsJsonAsync(basePath, new { content = "# Checkout\n" });

        var post = await client.PostAsJsonAsync($"{basePath}/chat", new { messages = new[] { new { role = "user", content = "Finaliza" } } });
        var requestId = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestId").GetString();

        var job = await PollChatJob(client, basePath, requestId!);

        Assert.Equal("done", job.GetProperty("status").GetString());
        Assert.True(job.GetProperty("finalized").GetBoolean());
        // Asserted against Written directly rather than round-tripping through GET: FakeBlobStore.ReadAsync
        // uses ConcurrentBag.LastOrDefault, whose enumeration order does not reliably reflect insertion
        // order once a path has been written more than once (the PUT above, then this chat save).
        Assert.Contains(factory.Blobs.Written, w => w.Content == complete);
    }

    [Theory]
    [InlineData("Resultado registrado na investigacao realizada com PowerShell:", true)]
    [InlineData("Dependencias: banco, fila, cache,", true)]
    [InlineData("Consulte a secao anterior;", true)]
    [InlineData("Sem dependencias externas -", true)]
    [InlineData("### 2.2 Proximos passos", true)]
    [InlineData("A pasta esta totalmente vazia.", false)]
    [InlineData("- item concluido", false)]
    public void LooksTruncatedFlagsSentencesThatEndOnOpenPunctuationOrABareHeading(string lastLine, bool expected)
    {
        var content = "# Checkout\n\n" + lastLine;
        Assert.Equal(expected, SpecProjectEndpoints.LooksTruncated(content));
    }

    [Fact]
    public void LooksTruncatedIsFalseForBlankContent()
    {
        Assert.False(SpecProjectEndpoints.LooksTruncated(""));
        Assert.False(SpecProjectEndpoints.LooksTruncated("   \n\n  "));
    }
}
