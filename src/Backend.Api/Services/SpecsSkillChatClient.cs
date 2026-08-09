using System.Net.Http.Headers;
using System.Text.Json;

namespace Backend.Api.Services;

/// <summary>
/// Proxies a chat turn to the "specs" skill/profile hosted on Hermes - same api_server/v1/chat/completions
/// mechanism already validated for the Analista DoR gate (AnalystDorGate.cs), pointed at a different
/// profile/model. Unlike AnalystDorGate this is a real conversational box (seção 5.2 update, "caixa como
/// OpenWebUI") - the reply is shown to the operator as-is, not parsed as a structured pass/fail result,
/// and it never writes to spec storage on its own; the operator decides what to keep via the separate
/// content editor/Salvar action.
/// </summary>
public sealed class SpecsSkillChatClient(IHttpClientFactory clients, IConfiguration configuration, ILogger<SpecsSkillChatClient> logger)
{
    public async Task<string?> SendAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var baseUrl = configuration["SpecsSkill:ApiServerBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogError("SpecsSkill:ApiServerBaseUrl is not configured");
            return null;
        }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl) || parsedBaseUrl.Scheme != Uri.UriSchemeHttps)
        {
            // Same rationale as AnalystDorGate: every message here can carry operator-authored spec
            // content, so a non-HTTPS destination is treated as a configuration mistake, not honored.
            logger.LogError("SpecsSkill:ApiServerBaseUrl must be an absolute https URL");
            return null;
        }
        var allowedHost = configuration["SpecsSkill:AllowedHost"];
        if (string.IsNullOrWhiteSpace(allowedHost) || !string.Equals(parsedBaseUrl.Host, allowedHost, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("SpecsSkill:ApiServerBaseUrl host {Host} does not match the configured SpecsSkill:AllowedHost", parsedBaseUrl.Host);
            return null;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "v1/chat/completions"))
            {
                Content = JsonContent.Create(new
                {
                    model = configuration["SpecsSkill:Model"] ?? "specs",
                    messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                }),
            };
            var key = configuration["SpecsSkill:ApiServerApiKey"];
            if (!string.IsNullOrWhiteSpace(key))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var r = await clients.CreateClient("SpecsSkill").SendAsync(req, ct);
            if (!r.IsSuccessStatusCode) return null;
            using var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            return j.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or UriFormatException or IndexOutOfRangeException)
        {
            logger.LogWarning(ex, "Specs skill chat call failed");
            return null;
        }
    }
}

public sealed record ChatMessage(string Role, string Content);
