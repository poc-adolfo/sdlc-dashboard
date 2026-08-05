using Microsoft.EntityFrameworkCore;
using SDLC.Dashboard;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<DashboardDb>(o => o.UseSqlite("Data Source=data/dashboard.db"));
builder.Services.AddScoped<IPlatformGateway, NoopPlatformGateway>();
builder.Services.AddScoped<ISecretStore, KubernetesSecretStore>();
builder.Services.AddScoped<CredentialRotationService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
var origins = builder.Configuration.GetSection("Security:AllowedOrigins").GetChildren().Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
builder.Services.AddCors(o => o.AddPolicy("configured-origins", p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
Directory.CreateDirectory("data");
using (var scope = app.Services.CreateScope()) scope.ServiceProvider.GetRequiredService<DashboardDb>().Database.EnsureCreated();
app.UseCors("configured-origins");
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        if (!SecurityRules.IsValidApiKey(context.Request.Headers["X-API-Key"].FirstOrDefault(), app.Configuration["Security:ApiKey"] ?? "")) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        if (string.IsNullOrWhiteSpace(TenantContext.Get(context))) { context.Response.StatusCode = StatusCodes.Status400BadRequest; await context.Response.WriteAsJsonAsync(new { message = "X-Tenant-Id é obrigatório." }); return; }
    }
    await next();
});
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/api/clients", async (HttpContext http, DashboardDb db) => Results.Ok(await db.Clients.Where(c => c.Workspaces.Any(w => w.TenantId == TenantContext.Get(http))).OrderBy(x => x.Name).ToListAsync()));
app.MapGet("/api/workspaces", async (HttpContext http, DashboardDb db) => Results.Ok(await db.Workspaces.Where(x => x.TenantId == TenantContext.Get(http) && x.Status == WorkspaceStatus.Active).Include(x => x.Client).OrderBy(x => x.Name).ToListAsync()));
app.MapPost("/api/workspaces", async (HttpContext http, WorkspaceInput input, DashboardDb db) => { var w = new Workspace { Name = input.Name, Slug = input.Slug, Platform = input.Platform, PlatformRef = input.PlatformRef, SpecsPath = string.IsNullOrWhiteSpace(input.SpecsPath) ? "specs/" : input.SpecsPath, ClientId = input.ClientId, TenantId = TenantContext.Get(http)! }; db.Workspaces.Add(w); await db.SaveChangesAsync(); return Results.Created($"/api/workspaces/{w.Id}", w); });

app.MapPut("/api/workspaces/{id:guid}", async (HttpContext http, Guid id, WorkspaceInput input, DashboardDb db) => { var w = await db.Workspaces.FindAsync(id); if (w == null) return Results.NotFound(); if (!SecurityRules.HasTenantAccess(TenantContext.Get(http), w.TenantId)) return Results.Forbid(); if ((w.Platform != input.Platform || w.PlatformRef != input.PlatformRef) && await db.Pipelines.AnyAsync(p => p.WorkspaceId == id)) return Results.Conflict(new { message = "platform e platform_ref não podem ser alterados após o primeiro ciclo." }); w.Name = input.Name; w.Slug = input.Slug; w.Platform = input.Platform; w.PlatformRef = input.PlatformRef; w.SpecsPath = input.SpecsPath; w.ClientId = input.ClientId; await db.SaveChangesAsync(); return Results.Ok(w); });
app.MapPost("/api/workspaces/{id:guid}/archive", async (HttpContext http, Guid id, DashboardDb db) => { var w = await db.Workspaces.FindAsync(id); if (w == null) return Results.NotFound(); if (!SecurityRules.HasTenantAccess(TenantContext.Get(http), w.TenantId)) return Results.Forbid(); w.Status = WorkspaceStatus.Archived; await db.SaveChangesAsync(); return Results.NoContent(); });

app.MapGet("/api/workspaces/{id:guid}/assessment", async (HttpContext http, Guid id, DashboardDb db) => { var w = await db.Workspaces.FindAsync(id); if (w == null) return Results.NotFound(); if (!SecurityRules.HasTenantAccess(TenantContext.Get(http), w.TenantId)) return Results.Forbid(); return Results.Ok(await db.Assessments.Where(x => x.WorkspaceId == id).OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync()); });
app.MapPut("/api/workspaces/{id:guid}/assessment", async (HttpContext http, Guid id, AssessmentInput input, DashboardDb db) => { var w = await db.Workspaces.FindAsync(id); if (w == null) return Results.NotFound(); if (!SecurityRules.HasTenantAccess(TenantContext.Get(http), w.TenantId)) return Results.Forbid(); var c = await db.Clients.FirstOrDefaultAsync(x => x.Name.ToLower() == input.ClientName.Trim().ToLower()); if (c == null) { c = new Client { Name = input.ClientName.Trim() }; db.Clients.Add(c); await db.SaveChangesAsync(); } var a = await db.Assessments.FirstOrDefaultAsync(x => x.WorkspaceId == id); if (a == null) { a = new Assessment { WorkspaceId = id }; db.Assessments.Add(a); } a.ClientId = c.Id; a.Content = string.IsNullOrWhiteSpace(input.Content) ? Domain.AssessmentTemplate : input.Content; a.Status = input.Completed ? AssessmentStatus.Completed : AssessmentStatus.InProgress; a.UpdatedAt = DateTimeOffset.UtcNow; if (input.Completed) w.ClientId = c.Id; await db.SaveChangesAsync(); return Results.Ok(a); });
app.MapGet("/api/workspaces/{id:guid}/specs", async (HttpContext http, Guid id, DashboardDb db) => { var w = await db.Workspaces.FindAsync(id); if (w == null) return Results.NotFound(); if (!SecurityRules.HasTenantAccess(TenantContext.Get(http), w.TenantId)) return Results.Forbid(); return Results.Ok(await db.Specs.Where(x => x.WorkspaceId == id && x.Status == SpecStatus.Draft).OrderBy(x => x.Title).ToListAsync()); });
app.MapGet("/api/workspaces/{id:guid}/dashboard", async (HttpContext http, Guid id, DashboardDb db) => { var w = await db.Workspaces.FindAsync(id); if (w == null) return Results.NotFound(); if (!SecurityRules.HasTenantAccess(TenantContext.Get(http), w.TenantId)) return Results.Forbid(); var phases = Domain.Phases.ToDictionary(x => x, _ => 0); foreach (var g in await db.Pipelines.Where(x => x.WorkspaceId == id).GroupBy(x => x.CurrentPhase).Select(x => new { x.Key, Count = x.Count() }).ToListAsync()) phases[g.Key] = g.Count; var pending = await db.Pipelines.Where(x => x.WorkspaceId == id && x.GateStatus == GateStatus.Pending).Select(x => new { x.Id, x.CurrentPhase, x.ExternalRef, x.GateApprover }).ToListAsync(); return Results.Ok(new { phases, pending }); });
app.MapPost("/api/workspaces/{id:guid}/specs/{specId:guid}/raise-us", async (HttpContext http, Guid id, Guid specId, DashboardDb db, IPlatformGateway platform) => { var w = await db.Workspaces.FindAsync(id); if (w == null) return Results.NotFound(); if (!SecurityRules.HasTenantAccess(TenantContext.Get(http), w.TenantId)) return Results.Forbid(); var s = await db.Specs.SingleOrDefaultAsync(x => x.Id == specId && x.WorkspaceId == id && x.Status == SpecStatus.Draft); if (s == null) return Results.NotFound(); var issue = await platform.CreateIssueAsync(w, s); var p = new PipelineInstance { WorkspaceId = id, SpecId = specId, CurrentPhase = Domain.Phases[0], GateStatus = GateStatus.Approved, ExternalRef = issue }; p.Transitions.Add(new PhaseTransition { Phase = p.CurrentPhase, SourceEvent = "cycle.created" }); db.Pipelines.Add(p); await db.SaveChangesAsync(); return Results.Created($"/api/pipelines/{p.Id}", p); });
app.MapPost("/api/webhooks/phase", async (HttpContext http, PhaseEvent e, DashboardDb db) => { var p = await db.Pipelines.Include(x => x.Workspace).SingleOrDefaultAsync(x => x.ExternalRef == e.ExternalRef); if (p == null) return Results.NotFound(); if (!SecurityRules.HasTenantAccess(TenantContext.Get(http), p.Workspace.TenantId)) return Results.Forbid(); if (p.CurrentPhase == e.TargetPhase) return Results.Ok(new { changed = false }); p.CurrentPhase = e.TargetPhase; p.GateStatus = e.GateStatus; p.Transitions.Add(new PhaseTransition { Phase = e.TargetPhase, SourceEvent = e.SourceEvent }); await db.SaveChangesAsync(); return Results.Ok(new { changed = true }); });

app.MapPost("/api/workspaces/{id:guid}/credentials", async (HttpContext http, Guid id, CredentialInput input, DashboardDb db, CredentialRotationService rotation, ISecretStore secrets) => { var w = await db.Workspaces.FindAsync(id); if (w == null) return Results.NotFound(); if (!SecurityRules.HasTenantAccess(TenantContext.Get(http), w.TenantId)) return Results.Forbid(); var c = await rotation.RotateAsync(db, secrets, id, input); return Results.Created($"/api/credentials/{c.Id}", new { c.Id, c.Profile, c.PlatformUsername, c.SecretRef, c.Scopes, c.Status }); });
app.MapGet("/api/workspaces/{id:guid}/credentials", async (HttpContext http, Guid id, DashboardDb db) => { var w = await db.Workspaces.FindAsync(id); if (w == null) return Results.NotFound(); if (!SecurityRules.HasTenantAccess(TenantContext.Get(http), w.TenantId)) return Results.Forbid(); return Results.Ok(await db.Credentials.Where(x => x.WorkspaceId == id).Select(x => new { x.Id, x.Profile, x.PlatformUsername, x.SecretRef, x.Scopes, x.Status, x.CreatedAt, x.RotatedAt }).ToListAsync()); });
app.Run();
public partial class Program { }
