using Amazon.S3;
using Azure.Storage.Blobs;
using Backend.Api.Apis;
using Backend.Api.Auth;
using Backend.Api.Services;
using Backend.Persistence.Data;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Net;
using System.Threading.RateLimiting;
using AuthOptions = Backend.Api.Auth.SessionOptions;

var builder = WebApplication.CreateBuilder(args);
var analistaBaseUrl = builder.Configuration["Analista:ApiServerBaseUrl"];
if (!Uri.TryCreate(analistaBaseUrl, UriKind.Absolute, out var parsedAnalistaUrl) ||
    parsedAnalistaUrl.Scheme != Uri.UriSchemeHttps &&
    !builder.Environment.IsDevelopment() &&
    !string.Equals(builder.Environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Analista:ApiServerBaseUrl must be an absolute HTTPS URL outside Development/Testing; this operator-controlled setting must not come from HTTP client input.");
// Same rationale as Analista above - SpecsSkill:ApiServerBaseUrl is deployment-controlled config, never
// HTTP input, but still shouldn't be allowed to silently point at plaintext HTTP outside dev/test.
var specsSkillBaseUrl = builder.Configuration["SpecsSkill:ApiServerBaseUrl"];
if (!Uri.TryCreate(specsSkillBaseUrl, UriKind.Absolute, out var parsedSpecsSkillUrl) ||
    parsedSpecsSkillUrl.Scheme != Uri.UriSchemeHttps &&
    !builder.Environment.IsDevelopment() &&
    !string.Equals(builder.Environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("SpecsSkill:ApiServerBaseUrl must be an absolute HTTPS URL outside Development/Testing; this operator-controlled setting must not come from HTTP client input.");
builder.Host.UseSerilog((ctx, log) => log.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());
builder.Services.AddOptions<AuthOptions>().Bind(builder.Configuration.GetSection(AuthOptions.SectionName)).Validate(options => !string.IsNullOrWhiteSpace(options.Username), "Authentication:Username is required").Validate(options => !string.IsNullOrWhiteSpace(options.Password), "Authentication:Password is required").Validate(options => AuthOptions.IsStrongSigningKey(options.SigningKey), "Authentication:SigningKey must be base64-encoded 32 bytes (256 bits)").Validate(options => options.LoginMaxFailures > 0, "Authentication:LoginMaxFailures must be positive").Validate(options => options.LoginFailureWindow > TimeSpan.Zero, "Authentication:LoginFailureWindow must be positive").Validate(options => options.LoginLockoutDuration > TimeSpan.Zero, "Authentication:LoginLockoutDuration must be positive").Validate(options => options.AccountLoginMaxFailures > 0, "Authentication:AccountLoginMaxFailures must be positive").Validate(options => options.AccountLoginLockoutDuration > TimeSpan.Zero, "Authentication:AccountLoginLockoutDuration must be positive").Validate(options => options.MaxTrackedLoginIdentities > 0, "Authentication:MaxTrackedLoginIdentities must be positive").Validate(options => options.LoginAttemptEntryTtl > TimeSpan.Zero, "Authentication:LoginAttemptEntryTtl must be positive").ValidateOnStart();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<LoginAttemptService>();
builder.Services.AddSingleton<Backend.Api.Services.AnalystDorGate>();
builder.Services.AddSingleton<Backend.Api.Services.PlatformContentClient>();
builder.Services.AddSingleton<Backend.Api.Services.ISecretStore, Backend.Api.Services.KubernetesSecretStore>();
builder.Services.AddSingleton<Backend.Api.Services.SpecsSkillChatClient>();
// SpecStorage:Provider picks the ISpecStorage backend - "azure" (default; Azurite locally, real Azure
// Blob Storage in production) or "s3" (the operator's separate S3 server, migration target - seção 5.2
// update). Same key convention either way ({clientId}/{projeto}/{fileName}), so this is the only place
// the choice is made; nothing else in the app knows which provider is active.
if (string.Equals(builder.Configuration["SpecStorage:Provider"], "s3", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IAmazonS3>(_ =>
    {
        var config = new AmazonS3Config { ForcePathStyle = true };
        var serviceUrl = builder.Configuration["SpecStorage:S3:ServiceUrl"];
        if (!string.IsNullOrWhiteSpace(serviceUrl)) config.ServiceURL = serviceUrl;
        var region = builder.Configuration["SpecStorage:S3:Region"];
        if (!string.IsNullOrWhiteSpace(region)) config.AuthenticationRegion = region;
        var accessKey = builder.Configuration["SpecStorage:S3:AccessKey"];
        var secretKey = builder.Configuration["SpecStorage:S3:SecretKey"];
        return string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(accessKey, secretKey, config);
    });
    builder.Services.AddSingleton<Backend.Api.Services.ISpecStorage>(sp =>
        new Backend.Api.Services.S3SpecStorage(sp.GetRequiredService<IAmazonS3>(), builder.Configuration["SpecStorage:S3:Bucket"] ?? "specs"));
}
else
{
    builder.Services.AddSingleton(_ => new BlobContainerClient(
        builder.Configuration["SpecStorage:Azure:ConnectionString"] ?? "UseDevelopmentStorage=true",
        builder.Configuration["SpecStorage:Azure:Container"] ?? "specs"));
    builder.Services.AddSingleton<Backend.Api.Services.ISpecStorage, Backend.Api.Services.AzureBlobSpecStorage>();
}
// Skipped in Testing: WebApplicationFactory-based tests construct ReconciliationPollerService directly
// and call RunOnceAsync deterministically instead of relying on its internal timer, so the automatic
// hosted-service loop would only add unwanted background platform calls during the test run.
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<Backend.Api.Services.ReconciliationPollerService>();
builder.Services.AddHttpClient("Platform");
// Analista:ApiServerBaseUrl is deployment-controlled configuration. It is never accepted from HTTP input;
// operators must restrict it to an approved internal Analista service (and protect its egress/network path).
builder.Services.AddHttpClient("Analista", (client, http) => { http.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Analista:TimeoutSeconds", 30)); }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("SpecsSkill", (client, http) => { http.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("SpecsSkill:TimeoutSeconds", 60)); }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? $"Data Source={builder.Configuration["DatabasePath"] ?? "workspace.db"}"));
// Security review on PR #15: /webhooks/* is public and unauthenticated-until-HMAC-checked, so besides
// the per-request body size cap it also needs a cap on request *volume* per source, or a caller without
// the signing secret can still burn memory/CPU/DB-lookup cost by firing many small requests. This is an
// app-level backstop (still worth pairing with ingress/WAF-level limiting in the k3s deployment, item
// 17 - not duplicated here) so the endpoint degrades instead of being wide open.
builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };
    options.AddPolicy("webhooks", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("Webhooks:RateLimit:PermitLimit", 30),
            Window = TimeSpan.FromSeconds(builder.Configuration.GetValue("Webhooks:RateLimit:WindowSeconds", 60)),
            QueueLimit = 0
        }));
});
builder.Services.AddOpenTelemetry().WithTracing(t => t.AddAspNetCoreInstrumentation().AddConsoleExporter()).WithMetrics(m => m.AddAspNetCoreInstrumentation().AddConsoleExporter());
builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen(c => { c.AddSecurityDefinition("sessionCookie", new Microsoft.OpenApi.Models.OpenApiSecurityScheme { Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey, In = Microsoft.OpenApi.Models.ParameterLocation.Cookie, Name = "sdlc_session" }); c.OperationFilter<SessionSecurityOperationFilter>(); });
var app = builder.Build();
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, KnownProxies = { IPAddress.Loopback } }); using (var scope = app.Services.CreateScope()) { scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
    // Best-effort convenience for local Azurite/first-run Azure Blob Storage - a real S3 bucket is
    // expected to already exist (provisioned by the operator), so this only runs for the Azure provider.
    var blobContainer = scope.ServiceProvider.GetService<BlobContainerClient>();
    if (blobContainer is not null) { try { blobContainer.CreateIfNotExists(); } catch (Exception ex) { Log.Warning(ex, "Could not ensure the spec storage container exists at startup"); } } }
app.UseSerilogRequestLogging(); app.UseRateLimiter(); app.UseMiddleware<SessionMiddleware>(); app.UseSwagger(); app.UseSwaggerUI(); app.MapAuthEndpoints(); app.MapWorkspaceEndpoints(); app.MapSpecUsEndpoints(); app.MapAssessmentEndpoints(); app.MapSpecStorageEndpoints(); app.MapCredentialEndpoints(); app.MapWebhookEndpoints(); app.MapPhaseTransitionEndpoints(); app.MapDashboardEndpoints(); app.MapHealthEndpoints();
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
