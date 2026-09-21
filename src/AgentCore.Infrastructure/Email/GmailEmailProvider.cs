using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Email;

public sealed class GmailEmailProvider(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<GmailEmailProvider> logger) : IEmailProvider
{
    public const string HttpClientName = "GmailEmail";

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(RefreshToken);

    private string? ClientId =>
        configuration["Gmail:ClientId"] ?? configuration["GMAIL_CLIENT_ID"];

    private string? ClientSecret =>
        configuration["Gmail:ClientSecret"] ?? configuration["GMAIL_CLIENT_SECRET"];

    private string? RefreshToken =>
        configuration["Gmail:RefreshToken"] ?? configuration["GMAIL_REFRESH_TOKEN"];

    public async ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest request, CancellationToken cancellationToken = default)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(request.Limit, 1, EmailToolLimits.MaxSearchLimit);
        var query = Uri.EscapeDataString(request.Query);
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://gmail.googleapis.com/gmail/v1/users/me/messages?q={query}&maxResults={limit}");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var body = GmailSensitiveRedactor.Redact(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Gmail search failed with status {StatusCode}", (int)response.StatusCode);
            throw new AgentCoreException("provider", "Gmail search failed.", 502);
        }

        using var document = JsonDocument.Parse(body);
        var results = new List<EmailSearchItem>();
        if (document.RootElement.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in messages.EnumerateArray())
            {
                if (!entry.TryGetProperty("id", out var idNode))
                {
                    continue;
                }

                var messageId = idNode.GetString() ?? "";
                if (messageId.Length == 0)
                {
                    continue;
                }

                var summary = await ReadMessageSummaryAsync(client, accessToken, messageId, cancellationToken)
                    .ConfigureAwait(false);
                if (summary is not null)
                {
                    results.Add(summary);
                }
            }
        }

        return new EmailSearchResult(results, false);
    }

    public async ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken cancellationToken = default)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{Uri.EscapeDataString(request.MessageId)}?format=full");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var body = GmailSensitiveRedactor.Redact(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Gmail read failed with status {StatusCode}", (int)response.StatusCode);
            throw AgentCoreErrors.NotFound("Message was not found.");
        }

        using var document = JsonDocument.Parse(body);
        return ParseMessage(document.RootElement);
    }

    public async ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest request, CancellationToken cancellationToken = default)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var raw = BuildRawMessage(request.To, request.Cc, request.Bcc, request.Subject, request.Body);
        var payload = JsonSerializer.Serialize(new { message = new { raw } });
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/drafts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var body = GmailSensitiveRedactor.Redact(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Gmail create draft failed with status {StatusCode}", (int)response.StatusCode);
            throw new AgentCoreException("provider", "Gmail draft creation failed.", 502);
        }

        using var document = JsonDocument.Parse(body);
        var draftId = document.RootElement.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? "" : "";
        if (draftId.Length == 0)
        {
            throw new AgentCoreException("provider", "Gmail draft creation returned no id.", 502);
        }

        var snapshot = await GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false)
            ?? new EmailDraftSnapshot(draftId, request.To, request.Cc, request.Bcc, request.Subject, request.Body);
        return new EmailDraftResult(snapshot);
    }

    public async ValueTask<EmailDraftSnapshot?> GetDraftAsync(string draftId, CancellationToken cancellationToken = default)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://gmail.googleapis.com/gmail/v1/users/me/drafts/{Uri.EscapeDataString(draftId)}?format=full");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var body = GmailSensitiveRedactor.Redact(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Gmail get draft failed with status {StatusCode}", (int)response.StatusCode);
            return null;
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("message", out var message))
        {
            return null;
        }

        var parsed = ParseMessage(message);
        return new EmailDraftSnapshot(
            draftId,
            parsed.To,
            parsed.Cc,
            [],
            parsed.Subject,
            parsed.Body);
    }

    public async ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest request, CancellationToken cancellationToken = default)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://gmail.googleapis.com/gmail/v1/users/me/drafts/{Uri.EscapeDataString(request.DraftId)}/send");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = GmailSensitiveRedactor.Redact(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(body);
                var messageId = document.RootElement.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
                return new EmailSendResult(EmailSendOutcome.Sent, messageId, null, null);
            }

            if ((int)response.StatusCode >= 500)
            {
                return new EmailSendResult(
                    EmailSendOutcome.Indeterminate,
                    null,
                    "provider",
                    "Gmail send outcome is unknown after a server error.");
            }

            return new EmailSendResult(EmailSendOutcome.Failed, null, "provider", "Gmail send was rejected.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Gmail send transport failure");
            return new EmailSendResult(
                EmailSendOutcome.Indeterminate,
                null,
                "transport",
                "Gmail send outcome is unknown after a transport failure.");
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId!,
                ["client_secret"] = ClientSecret!,
                ["refresh_token"] = RefreshToken!,
                ["grant_type"] = "refresh_token"
            })
        };
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = GmailSensitiveRedactor.Redact(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Gmail token refresh failed with status {StatusCode}", (int)response.StatusCode);
            throw new AgentCoreException("provider", "Gmail credentials are not valid.", 502);
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("access_token", out var tokenNode)
            ? tokenNode.GetString() ?? throw new AgentCoreException("provider", "Gmail token response was empty.", 502)
            : throw new AgentCoreException("provider", "Gmail token response was empty.", 502);
    }

    private async Task<EmailSearchItem?> ReadMessageSummaryAsync(
        HttpClient client,
        string accessToken,
        string messageId,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{Uri.EscapeDataString(messageId)}?format=metadata&metadataHeaders=Subject&metadataHeaders=From&metadataHeaders=Date");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var body = GmailSensitiveRedactor.Redact(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var subject = ReadHeader(root, "Subject") ?? "(no subject)";
        var from = ReadHeader(root, "From") ?? "";
        var dateText = ReadHeader(root, "Date");
        var date = DateTimeOffset.TryParse(dateText, out var parsed) ? parsed : DateTimeOffset.UtcNow;
        var snippet = root.TryGetProperty("snippet", out var snippetNode) ? snippetNode.GetString() ?? "" : "";
        var threadId = root.TryGetProperty("threadId", out var threadNode) ? threadNode.GetString() ?? "" : "";
        return new EmailSearchItem(messageId, threadId, from, subject, date, snippet);
    }

    private static EmailMessageResult ParseMessage(JsonElement message)
    {
        var messageId = message.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? "" : "";
        var threadId = message.TryGetProperty("threadId", out var threadNode) ? threadNode.GetString() ?? "" : "";
        var headers = message.TryGetProperty("payload", out var payload) && payload.TryGetProperty("headers", out var headersNode)
            ? headersNode
            : default;
        var subject = ReadHeader(headers, "Subject") ?? "";
        var from = ReadHeader(headers, "From") ?? "";
        var to = SplitAddresses(ReadHeader(headers, "To"));
        var cc = SplitAddresses(ReadHeader(headers, "Cc"));
        var dateText = ReadHeader(headers, "Date");
        var date = DateTimeOffset.TryParse(dateText, out var parsed) ? parsed : DateTimeOffset.UtcNow;
        var body = ExtractBody(message);
        var truncated = body.Length > EmailToolLimits.MaxBodyLength;
        if (truncated)
        {
            body = body[..EmailToolLimits.MaxBodyLength];
        }

        return new EmailMessageResult(
            messageId,
            threadId,
            from,
            to,
            cc,
            date,
            subject,
            body,
            truncated,
            []);
    }

    private static string? ReadHeader(JsonElement messageOrHeaders, string name)
    {
        if (messageOrHeaders.ValueKind == JsonValueKind.Object && messageOrHeaders.TryGetProperty("headers", out var headers))
        {
            return ReadHeader(headers, name);
        }

        if (messageOrHeaders.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var header in messageOrHeaders.EnumerateArray())
        {
            if (header.TryGetProperty("name", out var nameNode)
                && string.Equals(nameNode.GetString(), name, StringComparison.OrdinalIgnoreCase)
                && header.TryGetProperty("value", out var valueNode))
            {
                return valueNode.GetString();
            }
        }

        return null;
    }

    private static IReadOnlyList<string> SplitAddresses(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ExtractBody(JsonElement message)
    {
        if (!message.TryGetProperty("payload", out var payload))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        CollectBody(payload, builder);
        return builder.ToString();
    }

    private static void CollectBody(JsonElement payload, StringBuilder builder)
    {
        if (payload.TryGetProperty("body", out var body)
            && body.TryGetProperty("data", out var dataNode)
            && dataNode.GetString() is { Length: > 0 } data)
        {
            builder.Append(DecodeBase64Url(data));
        }

        if (payload.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                CollectBody(part, builder);
            }
        }
    }

    private static string DecodeBase64Url(string data)
    {
        var padded = data.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static string BuildRawMessage(
        IReadOnlyList<string> to,
        IReadOnlyList<string> cc,
        IReadOnlyList<string> bcc,
        string subject,
        string body)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"To: {string.Join(", ", to)}");
        if (cc.Count > 0)
        {
            builder.AppendLine($"Cc: {string.Join(", ", cc)}");
        }

        if (bcc.Count > 0)
        {
            builder.AppendLine($"Bcc: {string.Join(", ", bcc)}");
        }

        builder.AppendLine($"Subject: {subject}");
        builder.AppendLine("Content-Type: text/plain; charset=utf-8");
        builder.AppendLine();
        builder.Append(body);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(builder.ToString()))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
