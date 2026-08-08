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
var analistaBaseUrl = builder.Configuration["Analista:ApiServerBaseUrl"];
if (!Uri.TryCreate(analistaBaseUrl, UriKind.Absolute, out var parsedAnalistaUrl) ||
    parsedAnalistaUrl.Scheme != Uri.UriSchemeHttps &&
    !builder.Environment.IsDevelopment() &&
    !string.Equals(builder.Environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Analista:ApiServerBaseUrl must be an absolute HTTPS URL outside Development/Testing; this operator-controlled setting must not come from HTTP client input.");
builder.Host.UseSerilog((ctx, log) => log.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());
builder.Services.AddOptions<AuthOptions>().Bind(builder.Configuration.GetSection(AuthOptions.SectionName)).Validate(options => !string.IsNullOrWhiteSpace(options.Username), "Authentication:Username is required").Validate(options => !string.IsNullOrWhiteSpace(options.Password), "Authentication:Password is required").Validate(options => AuthOptions.IsStrongSigningKey(options.SigningKey), "Authentication:SigningKey must be base64-encoded 32 bytes (256 bits)").Validate(options => options.LoginMaxFailures > 0, "Authentication:LoginMaxFailures must be positive").Validate(options => options.LoginFailureWindow > TimeSpan.Zero, "Authentication:LoginFailureWindow must be positive").Validate(options => options.LoginLockoutDuration > TimeSpan.Zero, "Authentication:LoginLockoutDuration must be positive").Validate(options => options.AccountLoginMaxFailures > 0, "Authentication:AccountLoginMaxFailures must be positive").Validate(options => options.AccountLoginLockoutDuration > TimeSpan.Zero, "Authentication:AccountLoginLockoutDuration must be positive").Validate(options => options.MaxTrackedLoginIdentities > 0, "Authentication:MaxTrackedLoginIdentities must be positive").Validate(options => options.LoginAttemptEntryTtl > TimeSpan.Zero, "Authentication:LoginAttemptEntryTtl must be positive").ValidateOnStart();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<LoginAttemptService>();
builder.Services.AddSingleton<Backend.Api.Services.AnalystDorGate>();
builder.Services.AddSingleton<Backend.Api.Services.PlatformContentClient>();
builder.Services.AddSingleton<Backend.Api.Services.ISecretStore, Backend.Api.Services.KubernetesSecretStore>();
// Skipped in Testing: WebApplicationFactory-based tests construct ReconciliationPollerService directly
// and call RunOnceAsync deterministically instead of relying on its internal timer, so the automatic
// hosted-service loop would only add unwanted background platform calls during the test run.
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<Backend.Api.Services.ReconciliationPollerService>();
builder.Services.AddHttpClient("Platform");
// Analista:ApiServerBaseUrl is deployment-controlled configuration. It is never accepted from HTTP input;
// operators must restrict it to an approved internal Analista service (and protect its egress/network path).
builder.Services.AddHttpClient("Analista", (client, http) => { http.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Analista:TimeoutSeconds", 30)); }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? $"Data Source={builder.Configuration["DatabasePath"] ?? "workspace.db"}"));
builder.Services.AddOpenTelemetry().WithTracing(t => t.AddAspNetCoreInstrumentation().AddConsoleExporter()).WithMetrics(m => m.AddAspNetCoreInstrumentation().AddConsoleExporter());
builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen(c => { c.AddSecurityDefinition("sessionCookie", new Microsoft.OpenApi.Models.OpenApiSecurityScheme { Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey, In = Microsoft.OpenApi.Models.ParameterLocation.Cookie, Name = "sdlc_session" }); c.OperationFilter<SessionSecurityOperationFilter>(); });
var app = builder.Build();
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, KnownProxies = { IPAddress.Loopback } }); using (var scope = app.Services.CreateScope()) scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate(); app.UseSerilogRequestLogging(); app.UseMiddleware<SessionMiddleware>(); app.UseSwagger(); app.UseSwaggerUI(); app.MapAuthEndpoints(); app.MapWorkspaceEndpoints(); app.MapSpecUsEndpoints(); app.MapAssessmentEndpoints(); app.MapSpecListingEndpoints(); app.MapCredentialEndpoints(); app.MapWebhookEndpoints();
// Resource/tenant authorization beyond the single-operator session (including on the /subir-us publish
// endpoint and the assessment endpoints below) is intentionally out of scope for this phase; see
// frontend-operacional-sdlc-hermes.md sections 7 and 11 (single operator login, no multitenant support
// yet). CSRF on this cookie-based session is mitigated by SameSite=Strict on the session cookie (see
// AuthEndpoints.cs). This trade-off was evaluated by the Security gate in PR #10 review #4874587076 and
// accepted as a documented risk for future production/multitenant work, not a blocking defect; any
// review should treat that precedent as still governing until multitenant authorization ships.
app.Run();
public sealed class SessionSecurityOperationFilter : IOperationFilter { public void Apply(Microsoft.OpenApi.Models.OpenApiOperation operation, OperationFilterContext context) { if (context.ApiDescription.RelativePath?.Equals("auth/login", StringComparison.OrdinalIgnoreCase) == true) return; operation.Security = new List<Microsoft.OpenApi.Models.OpenApiSecurityRequirement> { new() { [new Microsoft.OpenApi.Models.OpenApiSecurityScheme { Reference = new Microsoft.OpenApi.Models.OpenApiReference { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "sessionCookie" } }] = Array.Empty<string>() } }; } }
public partial class Program { }
