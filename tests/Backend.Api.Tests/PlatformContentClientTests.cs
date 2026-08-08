using System.Net;
using System.Net.Http.Json;
using Backend.Api.Services;
using Backend.Persistence.Domain;
using Microsoft.Extensions.Configuration;

namespace Backend.Api.Tests;

/// <summary>Minimal IHttpClientFactory that always hands back the same HttpClient wrapping a RoutingFakeHandler - lets these tests construct PlatformContentClient directly instead of spinning up a full WebApplicationFactory.</summary>
public sealed class SingleClientHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

public sealed class PlatformContentClientTests
{
    private static PlatformContentClient BuildClient(RoutingFakeHandler handler, string? gitHubToken = "test-token") =>
        new(new SingleClientHttpClientFactory(handler),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["GitHub:AppToken"] = gitHubToken }).Build());

    private static Backend.Persistence.Domain.Workspace GitHubWorkspace(string platformRef = "acme/platform") => new() { Id = 1, Name = "W", Slug = "w", Platform = WorkspacePlatform.Github, PlatformRef = platformRef };
    private static Backend.Persistence.Domain.Workspace AzureDevOpsWorkspace() => new() { Id = 2, Name = "W", Slug = "w2", Platform = WorkspacePlatform.AzureDevOps, PlatformRef = "org/project" };

    [Fact]
    public async Task ListApprovedReviewerLoginsAsync_ReturnsOnlyReviewersWhoseLatestStateIsApproved()
    {
        var handler = new RoutingFakeHandler();
        handler.On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/acme/platform/pulls/42/reviews", _ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new object[]
                {
                    new { user = new { login = "revisor-bot" }, state = "APPROVED" },
                    new { user = new { login = "qa-bot" }, state = "APPROVED" },
                    new { user = new { login = "revisor-bot" }, state = "CHANGES_REQUESTED" } // supersedes the earlier APPROVED
                })
            });

        var result = await BuildClient(handler).ListApprovedReviewerLoginsAsync(GitHubWorkspace(), "42", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(new[] { "qa-bot" }, result);
    }

    [Fact]
    public async Task ListApprovedReviewerLoginsAsync_IgnoresEntriesMissingUserOrState()
    {
        var handler = new RoutingFakeHandler();
        handler.On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new object[] { new { state = "APPROVED" }, new { user = new { login = "no-state-bot" } } })
        });

        var result = await BuildClient(handler).ListApprovedReviewerLoginsAsync(GitHubWorkspace(), "42", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public async Task ListApprovedReviewerLoginsAsync_NonSuccessStatusReturnsNull()
    {
        var handler = new RoutingFakeHandler();
        handler.On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await BuildClient(handler).ListApprovedReviewerLoginsAsync(GitHubWorkspace(), "42", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ListApprovedReviewerLoginsAsync_MalformedJsonReturnsNull()
    {
        var handler = new RoutingFakeHandler();
        handler.On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not-json") });

        var result = await BuildClient(handler).ListApprovedReviewerLoginsAsync(GitHubWorkspace(), "42", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ListApprovedReviewerLoginsAsync_AzureDevOpsReturnsNullWithoutAnHttpCall()
    {
        var handler = new RoutingFakeHandler();
        handler.On(_ => true, _ => throw new InvalidOperationException("must not call the platform for Azure DevOps"));

        var result = await BuildClient(handler).ListApprovedReviewerLoginsAsync(AzureDevOpsWorkspace(), "42", CancellationToken.None);

        Assert.Null(result);
    }
}
