using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Backend.Api.Auth;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Backend.Api.Tests;

public sealed class AssessmentApiFactory : WebApplicationFactory<Program>
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"assessment-{Guid.NewGuid():N}.db");
    public FakeAnalistaHandler Handler { get; } = new();
    public string ApiKey { get; } = "test-token";
    public string TestPassword { get; } = Guid.NewGuid().ToString("N");
    public string TestSigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string,string?> {
            ["Authentication:Username"]="operator", ["Authentication:Password"] = TestPassword,
            ["Authentication:SigningKey"] = TestSigningKey, ["Authentication:SecureCookie"]="false",
            ["ConnectionStrings:Default"]=$"Data Source={path}", ["Analista:ApiServerBaseUrl"]="https://analista.test",
            ["Analista:AllowedHost"]="analista.test",
            ["Analista:ApiServerApiKey"] = ApiKey, ["Analista:TimeoutSeconds"] = "1" }))
        .ConfigureServices(s => s.AddHttpClient("Analista").ConfigurePrimaryHttpMessageHandler(() => Handler));
    protected override void ConfigureClient(HttpClient client) => client.DefaultRequestHeaders.Add("Cookie", "sdlc_session=" + Services.GetRequiredService<SessionService>().Create("operator", DateTimeOffset.UtcNow));
    protected override void Dispose(bool disposing) { base.Dispose(disposing); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach(var x in new[]{path,path+"-wal",path+"-shm"}) try { if(File.Exists(x)) File.Delete(x); } catch (IOException) { } }
}
public sealed class FakeAnalistaHandler : HttpMessageHandler
{
    public string Body { get; set; } = "{\"dor_atendido\":true,\"pendencias\":[]}";
    public bool Fail { get; set; }
    public bool Delay { get; set; }
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
    public string? Authorization { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Authorization = request.Headers.Authorization?.ToString();
        if (Delay) await Task.Delay(TimeSpan.FromSeconds(5), ct);
        if (Fail) throw new HttpRequestException("offline");
        return new(StatusCode) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Body } } } }), Encoding.UTF8, "application/json") };
    }
}

public sealed class AssessmentTests
{
    static AssessmentApiFactory New() => new();
    static async Task<(HttpClient C, long W, long A)> Seed(AssessmentApiFactory f, string name = "Client")
    { var c=f.CreateClient(); var w=await (await c.PostAsJsonAsync("/workspaces",new{name,platform="github",platform_ref=Guid.NewGuid().ToString()})).Content.ReadFromJsonAsync<Workspace>(); var a=await (await c.PostAsJsonAsync($"/workspaces/{w!.Id}/assessments",new{client_name=name,content="x"})).Content.ReadFromJsonAsync<Assessment>(); return (c,w.Id,a!.Id); }

    [Fact] public async Task EachFactoryHasIsolatedDatabaseAndHandler() { using var f=New(); var (c,_,_)=await Seed(f); Assert.Single(await c.GetFromJsonAsync<ClientResponse[]>("/clients")); }
    [Fact] public async Task ConcludeTrueUpdatesAssessmentAndWorkspace() { using var f=New(); var (c,w,a)=await Seed(f,"True"); var r=await c.PostAsync($"/workspaces/{w}/assessments/{a}/concluir",null); Assert.Equal(HttpStatusCode.OK,r.StatusCode); Assert.Contains("\"concluido\":true",await r.Content.ReadAsStringAsync()); var ar=await c.GetFromJsonAsync<AssessmentResponse>($"/workspaces/{w}/assessments/{a}"); var wr=await c.GetFromJsonAsync<WorkspaceResponse>($"/workspaces/{w}"); Assert.Equal("concluido",ar!.Status); Assert.Equal(ar.ClientId,wr!.ClientId); }
    [Fact] public async Task ConcludeFalseReturnsPendingAndPreservesState() { using var f=New(); f.Handler.Body="{\"dor_atendido\":false,\"pendencias\":[\"docs\"]}"; var (c,w,a)=await Seed(f,"False"); var r=await c.PostAsync($"/workspaces/{w}/assessments/{a}/concluir",null); Assert.Equal(HttpStatusCode.OK,r.StatusCode); Assert.Contains("docs",await r.Content.ReadAsStringAsync()); var ar=await c.GetFromJsonAsync<AssessmentResponse>($"/workspaces/{w}/assessments/{a}"); var wr=await c.GetFromJsonAsync<WorkspaceResponse>($"/workspaces/{w}"); Assert.Equal("em_andamento",ar!.Status); Assert.Null(wr!.ClientId); }
    [Fact] public async Task ConcludeFailurePreservesState() { using var f=New(); f.Handler.Fail=true; var (c,w,a)=await Seed(f,"Fail"); var r=await c.PostAsync($"/workspaces/{w}/assessments/{a}/concluir",null); Assert.Equal(HttpStatusCode.BadGateway,r.StatusCode); var ar=await c.GetFromJsonAsync<AssessmentResponse>($"/workspaces/{w}/assessments/{a}"); Assert.Equal("em_andamento",ar!.Status); Assert.Null((await c.GetFromJsonAsync<WorkspaceResponse>($"/workspaces/{w}"))!.ClientId); }
    [Fact]
    public async Task UpsertRejectsOversizedContentWithoutPersisting()
    {
        using var f = New(); var c = f.CreateClient();
        var w = await (await c.PostAsJsonAsync("/workspaces", new { name = "ContentLimit", platform = "github", platform_ref = Guid.NewGuid().ToString() })).Content.ReadFromJsonAsync<Workspace>();
        var response = await c.PostAsJsonAsync($"/workspaces/{w!.Id}/assessments", new { client_name = "ContentLimit", content = new string('x', 10001) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await c.GetFromJsonAsync<ClientResponse[]>("/clients") ?? Array.Empty<ClientResponse>());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/workspaces/{w.Id}/assessments/1")).StatusCode);
    }

    [Fact]
    public async Task UpsertRejectsOversizedClientNameWithoutPersisting()
    {
        using var f = New(); var c = f.CreateClient();
        var w = await (await c.PostAsJsonAsync("/workspaces", new { name = "NameLimit", platform = "github", platform_ref = Guid.NewGuid().ToString() })).Content.ReadFromJsonAsync<Workspace>();
        var response = await c.PostAsJsonAsync($"/workspaces/{w!.Id}/assessments", new { client_name = new string('x', 201), content = "valid" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await c.GetFromJsonAsync<ClientResponse[]>("/clients") ?? Array.Empty<ClientResponse>());
    }

    [Fact] public async Task UpsertValidationAndUpdateAndSearch() { using var f=New(); var c=f.CreateClient(); Assert.Equal(HttpStatusCode.UnprocessableEntity,(await c.PostAsJsonAsync("/workspaces/99/assessments",(object?)null)).StatusCode); Assert.Equal(HttpStatusCode.UnprocessableEntity,(await c.PostAsJsonAsync("/workspaces/99/assessments",new{})).StatusCode); var (c2,w,a)=await Seed(f,"Zed"); Assert.Equal(HttpStatusCode.UnprocessableEntity,(await c2.PostAsJsonAsync($"/workspaces/{w}/assessments",new{client_id=999})).StatusCode); Assert.Equal(HttpStatusCode.UnprocessableEntity,(await c2.PostAsJsonAsync($"/workspaces/{w}/assessments",new{client_name="  "})).StatusCode); Assert.Equal(HttpStatusCode.NotFound,(await c2.PostAsJsonAsync("/workspaces/99999/assessments",new{client_name="New"})).StatusCode); var u=await c2.PostAsJsonAsync($"/workspaces/{w}/assessments",new{client_name="Zed",content="updated"}); Assert.Equal(a,(await u.Content.ReadFromJsonAsync<Assessment>())!.Id); var all=await c2.GetFromJsonAsync<ClientResponse[]>("/clients"); Assert.Equal(new[]{"Zed"},all!.Select(x=>x.Name)); await c2.PostAsync($"/workspaces/{w}/assessments/{a}/concluir",null); Assert.Equal("Bearer test-token",f.Handler.Authorization); }
    [Fact]
    public async Task AssessmentCannotBeReadUpdatedOrConcludedThroughAnotherWorkspace()
    {
        using var f = New(); var (c, workspaceA, assessmentId) = await Seed(f, "A");
        var workspaceB = await (await c.PostAsJsonAsync("/workspaces", new { name = "B", platform = "github", platform_ref = Guid.NewGuid().ToString() })).Content.ReadFromJsonAsync<Workspace>();
        Assert.NotNull(workspaceB);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/workspaces/{workspaceB!.Id}/assessments/{assessmentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync($"/workspaces/{workspaceB.Id}/assessments", new { assessment_id = assessmentId, client_name = "B", content = "must not alter A" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync($"/workspaces/{workspaceB.Id}/assessments/{assessmentId}/concluir", null)).StatusCode);
        var original = await c.GetFromJsonAsync<AssessmentResponse>($"/workspaces/{workspaceA}/assessments/{assessmentId}");
        Assert.Equal("x", original!.Content); Assert.Equal("em_andamento", original.Status);
    }

    [Fact]
    public async Task OversizedAssessmentContentIsRejectedBeforeAnalistaCall()
    {
        using var f = New(); var c = f.CreateClient();
        var w = await (await c.PostAsJsonAsync("/workspaces", new { name = "Large", platform = "github", platform_ref = Guid.NewGuid().ToString() })).Content.ReadFromJsonAsync<Workspace>();
        var created = await c.PostAsJsonAsync($"/workspaces/{w!.Id}/assessments", new { client_name = "Large", content = new string('x', 10001) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, created.StatusCode);
        Assert.Empty(await c.GetFromJsonAsync<ClientResponse[]>("/clients") ?? Array.Empty<ClientResponse>());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/workspaces/{w.Id}/assessments/1")).StatusCode);
        Assert.Contains("maximum length", await created.Content.ReadAsStringAsync());
        Assert.Null(f.Handler.Authorization);
    }

    [Fact]
    public void ProductionRejectsHttpAnalistaUrlAtStartup()
    {
        using var factory = new ProductionHttpAnalistaFactory();
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Theory] [InlineData("{}")][InlineData("{\"dor_atendido\":true,\"pendencias\":\"bad\"}")][InlineData("{\"pendencias\":[]}")]
    public async Task InvalidGateResponsesAre502(string body) { using var f=New(); f.Handler.Body=body; var (c,w,a)=await Seed(f); Assert.Equal(HttpStatusCode.BadGateway,(await c.PostAsync($"/workspaces/{w}/assessments/{a}/concluir",null)).StatusCode); }
    [Fact] public async Task LastValidObjectWins() { using var f=New(); f.Handler.Body="{\"dor_atendido\":false,\"pendencias\":[\"old\"]} blah {\"dor_atendido\":true,\"pendencias\":[]}"; var (c,w,a)=await Seed(f); Assert.Contains("true",await (await c.PostAsync($"/workspaces/{w}/assessments/{a}/concluir",null)).Content.ReadAsStringAsync()); }
    [Fact] public async Task Non2xxAndTimeoutAre502() { using var f=New(); var (c,w,a)=await Seed(f); f.Handler.StatusCode=HttpStatusCode.ServiceUnavailable; Assert.Equal(HttpStatusCode.BadGateway,(await c.PostAsync($"/workspaces/{w}/assessments/{a}/concluir",null)).StatusCode); f.Handler.StatusCode=HttpStatusCode.OK; f.Handler.Fail=true; Assert.Equal(HttpStatusCode.BadGateway,(await c.PostAsync($"/workspaces/{w}/assessments/{a}/concluir",null)).StatusCode); f.Handler.Fail=false; f.Handler.Delay=true; Assert.Equal(HttpStatusCode.BadGateway,(await c.PostAsync($"/workspaces/{w}/assessments/{a}/concluir",null)).StatusCode); }
}
public sealed record Workspace(long Id); public sealed record Assessment(long Id,long ClientId,string Content); public sealed record AssessmentResponse(long Id,long WorkspaceId,long ClientId,string Content,string Status,DateTime CreatedAt,DateTime UpdatedAt); public sealed record WorkspaceResponse(long Id,string Name,string Slug,string Platform,string PlatformRef,long? ClientId,string Status,DateTime CreatedAt); public sealed record ClientResponse(long Id,string Name);

public sealed class ProductionHttpAnalistaFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseEnvironment("Production")
        .ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Analista:ApiServerBaseUrl"] = "http://analista.invalid" }));
}