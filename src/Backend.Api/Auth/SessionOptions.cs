namespace Backend.Api.Auth;

public sealed class SessionOptions
{
    public const string SectionName = "Authentication";
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required string SigningKey { get; init; }
    public string CookieName { get; init; } = "sdlc_session";
    public bool SecureCookie { get; init; } = true;
    public int ExpirationMinutes { get; init; } = 60;
    public int LoginMaxFailures { get; init; } = 5;
    public TimeSpan LoginFailureWindow { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan LoginLockoutDuration { get; init; } = TimeSpan.FromMinutes(15);
}
