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
