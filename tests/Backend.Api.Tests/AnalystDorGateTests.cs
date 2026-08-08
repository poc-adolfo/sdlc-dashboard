using Backend.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Api.Tests;

public sealed class AnalystDorGateTests
{
    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("HTTP client should not be created for a malformed base URL");
    }

    [Fact]
    public async Task MalformedBaseUrlReturnsNullInsteadOfThrowing()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Analista:ApiServerBaseUrl"] = "not a valid url" })
            .Build();
        var gate = new AnalystDorGate(new UnusedHttpClientFactory(), config, NullLogger<AnalystDorGate>.Instance);

        var result = await gate.CheckAsync("conteudo", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task NonHttpsBaseUrlIsRejectedWithoutMakingARequest()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Analista:ApiServerBaseUrl"] = "http://analista.internal" })
            .Build();
        var gate = new AnalystDorGate(new UnusedHttpClientFactory(), config, NullLogger<AnalystDorGate>.Instance);

        var result = await gate.CheckAsync("conteudo", CancellationToken.None);

        Assert.Null(result);
    }
}

public sealed class AnalystDorGateParseTests
{
    [Fact]
    public void ParsesPlainJsonWithoutCodeFence()
    {
        var result = AnalystDorGate.Parse("{\"dor_atendido\": true, \"pendencias\": []}");
        Assert.NotNull(result);
        Assert.True(result!.Attended);
        Assert.Empty(result.Pending);
    }

    [Fact]
    public void ParsesJsonWrappedInMarkdownCodeFenceAndSurroundingProse()
    {
        var text = "Aqui esta a avaliacao:\n```json\n{\"dor_atendido\": false, \"pendencias\": [\"falta X\", \"falta Y\"]}\n```\nObrigado.";
        var result = AnalystDorGate.Parse(text);
        Assert.NotNull(result);
        Assert.False(result!.Attended);
        Assert.Equal(new[] { "falta X", "falta Y" }, result.Pending);
    }

    [Fact]
    public void MalformedJsonReturnsNull()
    {
        Assert.Null(AnalystDorGate.Parse("{\"dor_atendido\": true, \"pendencias\": [}"));
    }

    [Fact]
    public void MissingDorAtendidoPropertyReturnsNull()
    {
        Assert.Null(AnalystDorGate.Parse("{\"pendencias\": []}"));
    }

    [Fact]
    public void MissingPendenciasPropertyReturnsNull()
    {
        Assert.Null(AnalystDorGate.Parse("{\"dor_atendido\": true}"));
    }

    [Theory]
    [InlineData("{\"dor_atendido\": \"true\", \"pendencias\": []}")]
    [InlineData("{\"dor_atendido\": 1, \"pendencias\": []}")]
    [InlineData("{\"dor_atendido\": null, \"pendencias\": []}")]
    public void NonBooleanDorAtendidoReturnsNullInsteadOfThrowing(string text)
    {
        Assert.Null(AnalystDorGate.Parse(text));
    }

    [Fact]
    public void NonStringItemInPendenciasReturnsNull()
    {
        Assert.Null(AnalystDorGate.Parse("{\"dor_atendido\": false, \"pendencias\": [\"ok\", 42]}"));
    }

    [Fact]
    public void PendenciasNotAnArrayReturnsNull()
    {
        Assert.Null(AnalystDorGate.Parse("{\"dor_atendido\": false, \"pendencias\": \"falta X\"}"));
    }

    [Fact]
    public void WhenMultipleJsonObjectsArePresentTheLastValidOneWins()
    {
        var text = "{\"dor_atendido\": true, \"pendencias\": []} depois reavaliei: {\"dor_atendido\": false, \"pendencias\": [\"na verdade falta Z\"]}";
        var result = AnalystDorGate.Parse(text);
        Assert.NotNull(result);
        Assert.False(result!.Attended);
        Assert.Equal("na verdade falta Z", result.Pending[0]);
    }

    [Fact]
    public void EmptyTextReturnsNull()
    {
        Assert.Null(AnalystDorGate.Parse(""));
    }
}
