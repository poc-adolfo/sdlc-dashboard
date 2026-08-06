using System.Net;
using System.Net.Http.Json;
using System.Text;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Backend.Api.Tests;

public sealed class AssessmentApiFactory : WebApplicationFactory<Program>
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"assessment-{Guid.NewGuid():N}.db");
    public FakeAnalistaHandler Handler { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string,string?> { ["Authentication:Username"]="operator", ["Authentication:Password"]="secret", ["Authentication:SigningKey"]="DoBqIVy5zTyTGicih2WShaYg6goTsq0lvS7XlPiHWps=", ["Authentication:SecureCookie"]="false", ["ConnectionStrings:Default"]=$"Data Source={path}", ["Analista:ApiServerBaseUrl"]="http://analista.test" })).ConfigureServices(s => { s.AddHttpClient("Analista").ConfigurePrimaryHttpMessageHandler(() => Handler); });
    protected override void ConfigureClient(HttpClient client) { client.DefaultRequestHeaders.Add("Cookie", "sdlc_session=" + Services.GetRequiredService<Backend.Api.Auth.SessionService>().Create("operator", DateTimeOffset.UtcNow)); }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); foreach(var x in new[]{path,path+"-wal",path+"-shm"}) if(File.Exists(x)) File.Delete(x); }
}
public sealed class FakeAnalistaHandler : HttpMessageHandler
{
    public string Body = "```json\n{\"dor_atendido\":true,\"pendencias\":[]}\n```"; public bool Fail;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Fail ? Task.FromException<HttpResponseMessage>(new HttpRequestException("offline")) : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Body } } } }), Encoding.UTF8, "application/json") });
}
public sealed class AssessmentTests : IClassFixture<AssessmentApiFactory>
{
    readonly AssessmentApiFactory f; public AssessmentTests(AssessmentApiFactory f)=>this.f=f;
    [Fact] public async Task SearchInlineCreateAndUpsert() { using var c=f.CreateClient(); var r=await c.GetAsync("/clients?q=Ac"); Assert.Equal(HttpStatusCode.OK,r.StatusCode); var created=await c.PostAsJsonAsync("/workspaces",new{name="A",platform="github",platform_ref="a"}); var w=await created.Content.ReadFromJsonAsync<Workspace>(); var a=await c.PostAsJsonAsync($"/workspaces/{w!.Id}/assessments",new{client_name="Acme",content="one"}); Assert.Equal(HttpStatusCode.OK,a.StatusCode); var first=await a.Content.ReadFromJsonAsync<Assessment>(); var b=await c.PostAsJsonAsync($"/workspaces/{w.Id}/assessments",new{client_id=first!.ClientId,content="two"}); var second=await b.Content.ReadFromJsonAsync<Assessment>(); Assert.Equal(first.Id,second!.Id); Assert.Equal("two",second.Content); }
    [Fact] public async Task ConcludeTrueFalseAndFailure() { using var c=f.CreateClient(); var w=await (await c.PostAsJsonAsync("/workspaces",new{name="B",platform="github",platform_ref="b"})).Content.ReadFromJsonAsync<Workspace>(); var a=await (await c.PostAsJsonAsync($"/workspaces/{w!.Id}/assessments",new{client_name="Beta",content="x"})).Content.ReadFromJsonAsync<Assessment>(); var ok=await c.PostAsync($"/workspaces/{w.Id}/assessments/{a!.Id}/concluir",null); Assert.Equal(HttpStatusCode.OK,ok.StatusCode); f.Handler.Body="{\"dor_atendido\":false,\"pendencias\":[\"x\"]}"; var again=await c.PostAsync($"/workspaces/{w.Id}/assessments/{a.Id}/concluir",null); Assert.Equal(HttpStatusCode.OK,again.StatusCode); f.Handler.Fail=true; Assert.Equal(HttpStatusCode.BadGateway,(await c.PostAsync($"/workspaces/{w.Id}/assessments/{a.Id}/concluir",null)).StatusCode); }
}
public sealed record Workspace(long Id); public sealed record Assessment(long Id,long ClientId,string Content);