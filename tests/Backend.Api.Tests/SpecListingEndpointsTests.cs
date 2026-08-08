using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Backend.Api.Apis;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Backend.Api.Tests;

public sealed class SpecListingEndpointsTests
{
    private static HttpResponseMessage GitHubDirectoryResponse(params (string Name, string Path, string Type)[] entries) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(entries.Select(e => new { name = e.Name, path = e.Path, type = e.Type }).ToArray())
    };

    private static HttpResponseMessage GitHubContentResponse(string markdown) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { content = Convert.ToBase64String(Encoding.UTF8.GetBytes(markdown)) })
    };

    private static async Task<HttpClient> AuthenticatedClient(SpecUsApplicationFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/auth/login", new { Username = "operator", Password = "secret" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<long> CreateGitHubWorkspace(HttpClient client, string specsPath = "")
    {
        var response = await client.PostAsJsonAsync("/workspaces", new { name = $"WS-{Guid.NewGuid():N}", platform = "github", platform_ref = "acme/platform", specs_path = specsPath });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt64();
    }

    private const string RascunhoSpec = "# Rascunho spec\n\n> Status: rascunho (2026-08-05).\n\nConteudo.\n";
    private const string PropostaSpec = "# Proposta spec\n\n> Status: proposta (2026-08-05).\n\nConteudo.\n";
    private const string AccentedRascunhoSpec = "# Acentuado\n\n> Status: Rascunho (2026-08-05).\n\nConteudo.\n";

    [Fact]
    public async Task ListingFiltersByStatusAndOnlyReturnsRascunho()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/", _ =>
            GitHubDirectoryResponse(("rascunho.md", "rascunho.md", "file"), ("proposta.md", "proposta.md", "file"), ("sub", "sub", "dir")));
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/rascunho.md", _ => GitHubContentResponse(RascunhoSpec));
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/proposta.md", _ => GitHubContentResponse(PropostaSpec));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        var response = await client.GetAsync($"/workspaces/{workspaceId}/specs?status=rascunho");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("rascunho.md", items[0].GetProperty("path").GetString());
        Assert.Equal("Rascunho spec", items[0].GetProperty("title").GetString());
        Assert.Equal("rascunho", items[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task ListingWithoutStatusFilterReturnsEverySpec()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/", _ =>
            GitHubDirectoryResponse(("rascunho.md", "rascunho.md", "file"), ("proposta.md", "proposta.md", "file")));
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/rascunho.md", _ => GitHubContentResponse(RascunhoSpec));
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/proposta.md", _ => GitHubContentResponse(PropostaSpec));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        var response = await client.GetAsync($"/workspaces/{workspaceId}/specs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, items.GetArrayLength());
    }

    [Fact]
    public async Task RepeatedListingDoesNotDuplicateSpecRowsAndUpdatesStatusOnChange()
    {
        using var factory = new SpecUsApplicationFactory();
        var body = RascunhoSpec;
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/", _ =>
            GitHubDirectoryResponse(("spec.md", "spec.md", "file")));
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/spec.md", _ => GitHubContentResponse(body));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        var first = await (await client.GetAsync($"/workspaces/{workspaceId}/specs")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, first.GetArrayLength());
        Assert.Equal(1, first[0].GetProperty("version").GetInt32());

        body = PropostaSpec;
        var second = await (await client.GetAsync($"/workspaces/{workspaceId}/specs")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, second.GetArrayLength());
        Assert.Equal("proposta", second[0].GetProperty("status").GetString());
        Assert.Equal(2, second[0].GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task StaleSpecIsRemovedWhenFileDisappearsFromDirectory()
    {
        using var factory = new SpecUsApplicationFactory();
        var currentFiles = new[] { ("a.md", "a.md", "file"), ("b.md", "b.md", "file") };
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/", _ => GitHubDirectoryResponse(currentFiles));
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/a.md", _ => GitHubContentResponse(RascunhoSpec));
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/b.md", _ => GitHubContentResponse(PropostaSpec));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        var first = await (await client.GetAsync($"/workspaces/{workspaceId}/specs")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, first.GetArrayLength());

        currentFiles = new[] { ("a.md", "a.md", "file") };
        var second = await (await client.GetAsync($"/workspaces/{workspaceId}/specs")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, second.GetArrayLength());
        Assert.Equal("a.md", second[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task StaleSpecReferencedByAPipelineInstanceIsNotRemoved()
    {
        using var factory = new SpecUsApplicationFactory();
        var currentFiles = new[] { ("a.md", "a.md", "file") };
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/", _ => GitHubDirectoryResponse(currentFiles));
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/a.md", _ => GitHubContentResponse(RascunhoSpec));
        factory.Handler.On(r => r.RequestUri!.Host == "analista.test", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { choices = new[] { new { message = new { content = "{\"dor_atendido\": true, \"pendencias\": []}" } } } })
        });
        factory.Handler.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues"), _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { number = 1 }) });

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        await client.GetAsync($"/workspaces/{workspaceId}/specs"); // syncs the spec index
        var publish = await client.PostAsync($"/workspaces/{workspaceId}/specs/a.md/subir-us", content: null);
        Assert.Equal(HttpStatusCode.Created, publish.StatusCode);

        currentFiles = Array.Empty<(string, string, string)>();
        var afterRemoval = await (await client.GetAsync($"/workspaces/{workspaceId}/specs")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, afterRemoval.GetArrayLength());
        Assert.Equal("a.md", afterRemoval[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task DirectoryListingFailureReturnsBadGateway()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/", _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        var response = await client.GetAsync($"/workspaces/{workspaceId}/specs");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task UnreadableFileIsSkippedRatherThanFailingTheWholeListing()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/", _ =>
            GitHubDirectoryResponse(("broken.md", "broken.md", "file"), ("ok.md", "ok.md", "file")));
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/broken.md", _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/ok.md", _ => GitHubContentResponse(RascunhoSpec));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client);

        var response = await client.GetAsync($"/workspaces/{workspaceId}/specs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("ok.md", items[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task MissingWorkspaceReturnsNotFound()
    {
        using var factory = new SpecUsApplicationFactory();
        using var client = await AuthenticatedClient(factory);

        var response = await client.GetAsync("/workspaces/999999/specs");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConfiguredSpecsPathIsUsedAsTheListedDirectory()
    {
        using var factory = new SpecUsApplicationFactory();
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/specs", _ =>
            GitHubDirectoryResponse(("spec.md", "specs/spec.md", "file")));
        factory.Handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/contents/specs/spec.md", _ => GitHubContentResponse(RascunhoSpec));

        using var client = await AuthenticatedClient(factory);
        var workspaceId = await CreateGitHubWorkspace(client, specsPath: "specs/");

        var response = await client.GetAsync($"/workspaces/{workspaceId}/specs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("specs/spec.md", items[0].GetProperty("path").GetString());
    }
}

public sealed class SpecListingParsingTests
{
    [Theory]
    [InlineData("> Status: rascunho (2026-08-05).", "rascunho")]
    [InlineData("> Status: Rascunho (2026-08-05).", "rascunho")]
    [InlineData("> Status: proposta (2026-08-05).", "proposta")]
    [InlineData("> Status:   implementado   (2026-08-05).", "implementado")]
    public void ParseStatusNormalizesCaseAndWhitespace(string blockquote, string expected)
    {
        Assert.Equal(expected, SpecListingEndpoints.ParseStatus($"# Titulo\n\n{blockquote}\n"));
    }

    [Fact]
    public void ParseStatusNormalizesAccents()
    {
        Assert.Equal("rascunho", SpecListingEndpoints.ParseStatus("> Status: Rascunho (2026-08-05).\ncontent with çedilha and accents áéí"));
    }

    [Fact]
    public void ParseStatusReturnsNullWhenBlockquoteIsMissing()
    {
        Assert.Null(SpecListingEndpoints.ParseStatus("# Titulo\n\nSem blockquote de status."));
    }

    [Fact]
    public void ParseTitleUsesFirstHeading()
    {
        Assert.Equal("Meu Titulo", SpecListingEndpoints.ParseTitle("# Meu Titulo\n\n> Status: rascunho (2026-08-05).", "specs/x.md"));
    }

    [Fact]
    public void ParseTitleFallsBackToFileNameWhenHeadingIsMissing()
    {
        Assert.Equal("x", SpecListingEndpoints.ParseTitle("> Status: rascunho (2026-08-05).", "specs/x.md"));
    }
}
