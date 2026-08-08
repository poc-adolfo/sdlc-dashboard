using System.Net.Http.Headers;
using System.Text.Json;

namespace Backend.Api.Services;

public sealed class AnalystDorGate(IHttpClientFactory clients, IConfiguration configuration, ILogger<AnalystDorGate> logger)
{
    public async Task<DorResult?> CheckAsync(string content, CancellationToken ct)
    {
        var baseUrl = configuration["Analista:ApiServerBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogError("Analista:ApiServerBaseUrl is not configured");
            return null;
        }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl) || parsedBaseUrl.Scheme != Uri.UriSchemeHttps)
        {
            // Analista:ApiServerBaseUrl carries the full spec/assessment content plus an optional
            // bearer key on every call; refusing non-HTTPS destinations bounds SSRF/credential-leak
            // blast radius to configuration mistakes rather than plaintext exfiltration.
            logger.LogError("Analista:ApiServerBaseUrl must be an absolute https URL");
            return null;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "v1/chat/completions"))
            {
                Content = JsonContent.Create(new { model = "analista", messages = new[] { new { role = "user", content } } })
            };
            var key = configuration["Analista:ApiServerApiKey"];
            if (!string.IsNullOrWhiteSpace(key))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var r = await clients.CreateClient("Analista").SendAsync(req, ct);
            r.EnsureSuccessStatusCode();
            using var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            return Parse(j.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or UriFormatException)
        {
            logger.LogWarning(ex, "Analista DoR gate failed");
            return null;
        }
    }

    public static DorResult? Parse(string text)
    {
        for (var end = text.Length; end > 0; end--)
        {
            var start = text.LastIndexOf('{', end - 1);
            if (start < 0) break;
            try
            {
                using var d = JsonDocument.Parse(text[start..end]);
                var r = d.RootElement;
                if (!r.TryGetProperty("dor_atendido", out var a) || !r.TryGetProperty("pendencias", out var p) || p.ValueKind != JsonValueKind.Array)
                    continue;
                var vals = p.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : null).ToList();
                if (vals.Any(x => x is null)) return null;
                return new(a.GetBoolean(), vals!);
            }
            catch (JsonException) { }
        }
        return null;
    }
}

public sealed record DorResult(bool Attended, IReadOnlyList<string> Pending);
