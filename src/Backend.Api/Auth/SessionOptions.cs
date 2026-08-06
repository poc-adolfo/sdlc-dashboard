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
    public int AccountLoginMaxFailures { get; init; } = 5;
    public TimeSpan AccountLoginLockoutDuration { get; init; } = TimeSpan.FromMinutes(15);
    public int MaxTrackedLoginIdentities { get; init; } = 10_000;
    public TimeSpan LoginAttemptEntryTtl { get; init; } = TimeSpan.FromMinutes(10);

    // This validates the HMAC key's encoding and size only. It cannot prove randomness
    // or entropy; production deployments must generate the value with a CSPRNG (for
    // example, `openssl rand -base64 32`) and inject it through a secret manager.
    public static bool IsStrongSigningKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Distinct().Count() < 8) return false;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException) { return false; }
        if (bytes.Length != 32) return false;
        if (bytes.All(b => b == bytes[0])) return false;
        var ascending = bytes.Zip(bytes.Skip(1), (a, b) => b == a + 1).All(x => x);
        var descending = bytes.Zip(bytes.Skip(1), (a, b) => b + 1 == a).All(x => x);
        return !ascending && !descending;
    }
}
