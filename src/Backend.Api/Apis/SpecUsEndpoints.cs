using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Backend.Api.Services;
using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Apis;

public static class SpecUsEndpoints
{
 public static IEndpointRouteBuilder MapSpecUsEndpoints(this IEndpointRouteBuilder e) { e.MapPost("/workspaces/{id:long}/specs/{**path}", Handle); return e; }
 private static async Task<IResult> Handle(long id, string path, AppDbContext db, IHttpClientFactory clients, AnalystDorGate gate, IConfiguration cfg, ILoggerFactory logs, CancellationToken ct)
 {
  if (!path.EndsWith("/subir-us", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
  var specPath = path[..^"/subir-us".Length].TrimStart('/');
  var w = await db.Workspaces.SingleOrDefaultAsync(x=>x.Id==id,ct); if (w is null) return Results.NotFound();
  var fullPath = string.IsNullOrWhiteSpace(w.SpecsPath) ? specPath : w.SpecsPath.TrimEnd('/') + "/" + specPath;
  var repo = w.SpecsRepo ?? w.PlatformRef;
  var fetched = await Fetch(w, repo, fullPath, clients, cfg, ct);
  if (fetched is null) return Results.StatusCode(502);
  var dor = await gate.CheckAsync(fetched, ct); if (dor is null) return Results.StatusCode(502);
  if (!dor.Attended) return Results.Ok(new { dor_atendido=false, pendencias=dor.Pending });
  var body = Extract(fetched, logs.CreateLogger("SpecUsEndpoints"));
  var title = Regex.Match(fetched, @"(?m)^#\s+(.+?)\s*$").Groups[1].Value.Trim(); if (title.Length==0) title="Sem titulo";
  if (w.SpecsRepo is not null && !string.Equals(w.SpecsRepo,w.PlatformRef,StringComparison.OrdinalIgnoreCase)) body += $"\n\n---\n\n<details>\n<summary>Spec completa: {specPath}</summary>\n\n```markdown\n{fetched}\n```\n\n</details>";
  var external = await Publish(w, title, body, clients, cfg, ct); if (external is null) return Results.StatusCode(502);
  var spec = await db.Specs.SingleOrDefaultAsync(x=>x.WorkspaceId==id && x.Path==specPath,ct);
  var pipeline = new PipelineInstance { WorkspaceId=id, SpecId=spec?.Id, FaseAtual=PipelinePhase.Requisitos, GateStatus=GateStatus.Approved, ExternalRef=external, CreatedAt=DateTime.UtcNow };
  db.PipelineInstances.Add(pipeline); await db.SaveChangesAsync(ct);
  return Results.Json(new { pipeline_instance = pipeline }, statusCode: 201);
 }
 private static async Task<string?> Fetch(Workspace w,string repo,string path,IHttpClientFactory f,IConfiguration c,CancellationToken ct)
 { try { using var req = new HttpRequestMessage(HttpMethod.Get, w.Platform==WorkspacePlatform.Github ? $"https://api.github.com/repos/{repo}/contents/{Uri.EscapeDataString(path).Replace("%2F","/\")}" : AdoUrl(repo,path)); AddAuth(req,w.Platform,c); using var r=await f.CreateClient("Platform").SendAsync(req,ct); if(!r.IsSuccessStatusCode)return null; if(w.Platform==WorkspacePlatform.Github){var x=await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:ct); return Encoding.UTF8.GetString(Convert.FromBase64String(x.GetProperty("content").GetString()!.Replace("\n","")));} var a=await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:ct); return a.TryGetProperty("content",out var content)?content.GetString():await r.Content.ReadAsStringAsync(ct); } catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or JsonException or FormatException){return null;} }
 private static async Task<string?> Publish(Workspace w,string title,string body,IHttpClientFactory f,IConfiguration c,CancellationToken ct) { try { HttpRequestMessage req; if(w.Platform==WorkspacePlatform.Github){req=new(HttpMethod.Post,$"https://api.github.com/repos/{w.PlatformRef}/issues"){Content=JsonContent.Create(new{title="US: "+title,body})};} else {var parts=w.PlatformRef.Split('/',StringSplitOptions.RemoveEmptyEntries);if(parts.Length!=2)return null;var type=Uri.EscapeDataString(w.AdoWorkItemType??"User Story");req=new(HttpMethod.Post,$"https://dev.azure.com/{parts[0]}/{parts[1]}/_apis/wit/workitems/${type}?api-version=7.1"){Content=new StringContent(JsonSerializer.Serialize(new[]{new{op="add",path="/fields/System.Title",value="US: "+title},new{op="add",path="/fields/System.Description",value=Html(body)}}),Encoding.UTF8,"application/json-patch+json")};} AddAuth(req,w.Platform,c);using var r=await f.CreateClient("Platform").SendAsync(req,ct);if(!r.IsSuccessStatusCode)return null;var x=await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);return (w.Platform==WorkspacePlatform.Github?x.GetProperty("number"):x.GetProperty("id")).ToString();}catch(Exception ex)when(ex is HttpRequestException or TaskCanceledException or JsonException){return null;} }
 private static void AddAuth(HttpRequestMessage r,WorkspacePlatform p,IConfiguration c){var token=c[p==WorkspacePlatform.Github?"GitHub:AppToken":"AzureDevOps:AppToken"];if(string.IsNullOrWhiteSpace(token))return; // Temporario: tokens globais; a tarefa 8 substituirá por workspace.app_secret_ref/Kubernetes Secret.
  if(p==WorkspacePlatform.Github)r.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);else r.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.ASCII.GetBytes(":"+token)));r.Headers.UserAgent.ParseAdd("sdlc-dashboard");}
 private static string AdoUrl(string repo,string path){var p=repo.Split('/',3,StringSplitOptions.RemoveEmptyEntries);return p.Length==3?$"https://dev.azure.com/{p[0]}/{p[1]}/_apis/git/repositories/{p[2]}/items?path=/{Uri.EscapeDataString(path).Replace("%2F","/")}&includeContent=true&api-version=7.1":"https://invalid/";}
 internal static string Extract(string s,ILogger l){var user=Regex.Match(s,@"(?ms)^##\s+User Story\s*\n(?<x>.*?)(?=^##\s|\z)",RegexOptions.Multiline).Groups["x"].Value.Trim();var ac=Regex.Match(s,@"(?ms)^##\s+Criterios de aceite\s*\n(?<x>.*?)(?=^##\s|\z)",RegexOptions.Multiline).Groups["x"].Value.Trim();var w=Regex.Match(s,@"(?ms)^##\s+WBS[^\n]*\n(?<x>.*?)(?=^##\s|\z)",RegexOptions.Multiline).Groups["x"].Value.Trim();if(user=="")l.LogWarning("Spec extraction section missing: User Story");if(ac=="")l.LogWarning("Spec extraction section missing: Criterios de aceite");if(w=="")l.LogWarning("Spec extraction section missing: WBS");return $"## User Story\n{user}\n\n## Criterios de aceite\n{ac}\n\n## WBS - Plano de implementacao\n{w}";}
 internal static string Html(string x){var sb=new StringBuilder();foreach(var line in x.Split('\n')){if(line.StartsWith("## "))sb.Append("<h2>").Append(System.Net.WebUtility.HtmlEncode(line[3..])).Append("</h2>");else if(line.StartsWith("- "))sb.Append("<li>").Append(Regex.Replace(System.Net.WebUtility.HtmlEncode(line[2..]),@"\*\*(.+?)\*\*","<strong>$1</strong>")).Append("</li>");else if(line.Trim()!="")sb.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(line)).Append("</p>");}return sb.ToString();}
}
