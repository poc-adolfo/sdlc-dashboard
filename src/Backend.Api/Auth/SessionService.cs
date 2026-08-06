using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Backend.Api.Auth;

public sealed class SessionService(IOptions<SessionOptions> options)
{
    private readonly SessionOptions _options = options.Value;
    private readonly byte[] _signingKey = Convert.FromBase64String(options.Value.SigningKey);

    public bool ValidateCredentials(string username, string password) =>
        Fixed(username, _options.Username) && Fixed(password, _options.Password);

    public string Create(string username, DateTimeOffset now)
    {
        var expires = now.AddMinutes(_options.ExpirationMinutes).ToUnixTimeSeconds();
        var payload = $"{username}|{expires}";
        return $"{payload}|{Sign(payload)}";
    }

    public bool Validate(string? value, DateTimeOffset now, out string? username)
    {
        username = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('|');
        if (parts.Length != 3 || !long.TryParse(parts[1], out var expiration) || expiration <= now.ToUnixTimeSeconds()) return false;
        if (string.IsNullOrWhiteSpace(parts[0]) || !Fixed(parts[2], Sign($"{parts[0]}|{parts[1]}"))) return false;
        username = parts[0];
        return true;
    }

    private string Sign(string value) => Convert.ToBase64String(
        HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(value)));

    private static bool Fixed(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
