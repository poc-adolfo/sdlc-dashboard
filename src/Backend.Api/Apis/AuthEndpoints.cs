using Backend.Api.Auth;
using Microsoft.Extensions.Options;

namespace Backend.Api.Apis;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", (LoginRequest? request, HttpContext http, SessionService sessions, LoginAttemptService attempts, IOptions<Backend.Api.Auth.SessionOptions> options, ILoggerFactory logs) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = new[] { "Username and Password are required." }
                }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var username = request.Username.Trim().ToUpperInvariant();
            var identity = $"ip:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}|{username}";
            var accountIdentity = $"account:{username}";
            var now = DateTimeOffset.UtcNow;
            var isIpBlocked = attempts.IsBlocked(identity, now);
            var isAccountBlocked = attempts.IsAccountBlocked(username, now);
            if (isIpBlocked || isAccountBlocked)
            {
                var lockoutDuration = isAccountBlocked
                    ? options.Value.AccountLoginLockoutDuration
                    : options.Value.LoginLockoutDuration;
                http.Response.Headers.RetryAfter = ((int)Math.Ceiling(lockoutDuration.TotalSeconds)).ToString();
                logs.CreateLogger("Auth").LogWarning("Login rejected because the client is temporarily locked out from repeated failures; IP={RemoteIp}", http.Connection.RemoteIpAddress);
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            if (!sessions.ValidateCredentials(request.Username, request.Password))
            {
                attempts.RecordFailure(identity, now);
                attempts.RecordAccountFailure(username, now);
                logs.CreateLogger("Auth").LogWarning("Invalid login attempt; IP={RemoteIp}", http.Connection.RemoteIpAddress);
                return Results.Unauthorized();
            }

            attempts.RecordSuccess(identity, accountIdentity);
            var cookie = options.Value;
            http.Response.Cookies.Append(cookie.CookieName, sessions.Create(request.Username, now), new CookieOptions { HttpOnly = true, Secure = cookie.SecureCookie, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromMinutes(cookie.ExpirationMinutes), IsEssential = true });
            logs.CreateLogger("Auth").LogInformation("Successful operator login; IP={RemoteIp}", http.Connection.RemoteIpAddress);
            return Results.Ok(new { authenticated = true });
        }).AllowAnonymous().WithName("Login");
        return app;
    }

    public sealed record LoginRequest(string Username, string Password);
}
