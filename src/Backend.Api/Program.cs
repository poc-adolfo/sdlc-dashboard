using Backend.Api.Apis;
using Backend.Api.Auth;
using Backend.Persistence.Data;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Net;
using AuthOptions = Backend.Api.Auth.SessionOptions;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, log) => log.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());
builder.Services.AddOptions<AuthOptions>().Bind(builder.Configuration.GetSection(AuthOptions.SectionName)).Validate(options => !string.IsNullOrWhiteSpace(options.Username), "Authentication:Username is required").Validate(options => !string.IsNullOrWhiteSpace(options.Password), "Authentication:Password is required").Validate(options => AuthOptions.IsStrongSigningKey(options.SigningKey), "Authentication:SigningKey must be base64-encoded 32 bytes (256 bits)").Validate(options => options.LoginMaxFailures > 0, "Authentication:LoginMaxFailures must be positive").Validate(options => options.LoginFailureWindow > TimeSpan.Zero, "Authentication:LoginFailureWindow must be positive").Validate(options => options.LoginLockoutDuration > TimeSpan.Zero, "Authentication:LoginLockoutDuration must be positive").Validate(options => options.AccountLoginMaxFailures > 0, "Authentication:AccountLoginMaxFailures must be positive").Validate(options => options.AccountLoginLockoutDuration > TimeSpan.Zero, "Authentication:AccountLoginLockoutDuration must be positive").Validate(options => options.MaxTrackedLoginIdentities > 0, "Authentication:MaxTrackedLoginIdentities must be positive").Validate(options => options.LoginAttemptEntryTtl > TimeSpan.Zero, "Authentication:LoginAttemptEntryTtl must be positive").ValidateOnStart();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<LoginAttemptService>();
builder.Services.AddSingleton<Backend.Api.Services.AnalystDorGate>();
builder.Services.AddHttpClient("Platform");
builder.Services.AddHttpClient("Analista");
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? $"Data Source={builder.Configuration["DatabasePath"] ?? "workspace.db"}"));
builder.Services.AddOpenTelemetry().WithTracing(t => t.AddAspNetCoreInstrumentation().AddConsoleExporter()).WithMetrics(m => m.AddAspNetCoreInstrumentation().AddConsoleExporter());
builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen(c => { c.AddSecurityDefinition("sessionCookie", new Microsoft.OpenApi.Models.OpenApiSecurityScheme { Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey, In = Microsoft.OpenApi.Models.ParameterLocation.Cookie, Name = "sdlc_session" }); c.OperationFilter<SessionSecurityOperationFilter>(); });
var app = builder.Build();
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, KnownProxies = { IPAddress.Loopback } }); using (var scope = app.Services.CreateScope()) scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate(); app.UseSerilogRequestLogging(); app.UseMiddleware<SessionMiddleware>(); app.UseSwagger(); app.UseSwaggerUI(); app.MapAuthEndpoints(); app.MapWorkspaceEndpoints(); app.MapSpecUsEndpoints(); app.Run();
public sealed class SessionSecurityOperationFilter : IOperationFilter { public void Apply(Microsoft.OpenApi.Models.OpenApiOperation operation, OperationFilterContext context) { if (context.ApiDescription.RelativePath?.Equals("auth/login", StringComparison.OrdinalIgnoreCase) == true) return; operation.Security = new List<Microsoft.OpenApi.Models.OpenApiSecurityRequirement> { new() { [new Microsoft.OpenApi.Models.OpenApiSecurityScheme { Reference = new Microsoft.OpenApi.Models.OpenApiReference { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "sessionCookie" } }] = Array.Empty<string>() } }; } }
public partial class Program { }
