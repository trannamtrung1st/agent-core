using System.Net;
using AgentCore.Application.Observability;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using MimeKit;

namespace AgentCore.Infrastructure.Email;

// One admitted account; Core owns drafts. No process-global credential or provider Draft fallback.
public sealed class GmailEmailProvider(EmailAccountContext account, IHttpClientFactory clients,
    Func<CancellationToken, ValueTask<string>> token, Func<CancellationToken, ValueTask> recheck,
    Func<bool> sendEnabled, TimeProvider time, ILogger<GmailEmailProvider>? logger = null, IDiagnosticIdSource? diagnosticIds = null, Func<CancellationToken, ValueTask<string>>? freshToken = null, Func<CancellationToken, ValueTask>? rejectAuthorization = null) : IEmailProvider, IEmailSendPreflight
{
    public const string HttpClientName = "GmailEmail";
    public bool IsAvailable => account.Profile.IsUsable;
    private string Prefix => EmailMessageReferences.GmailPrefix(account.AccountId);
    private string? preparedToken;
    public async ValueTask PrepareSendAsync(CancellationToken ct)
    {
        using var observation = new EmailOperationObservation("send.preflight", account.Profile.Synthetic, time, account.AccountId, ct);
        if (!sendEnabled() || !account.Profile.CanSend) throw AgentCoreErrors.Forbidden("email_send_disabled: Sending is disabled or Gmail send permission is missing.");
        await recheck(ct); preparedToken = await (freshToken ?? token)(ct); await recheck(ct);
        observation.Complete();
    }
    public async ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest request, CancellationToken cancellationToken = default)
    {
        using var observation = new EmailOperationObservation("search", account.Profile.Synthetic, time, account.AccountId, cancellationToken);
        try { var result = await SearchCoreAsync(request, cancellationToken); observation.Complete(); return result; }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested) { observation.Fail(ex); throw; }
        catch (Exception ex)
        {
            var failure = EmailOperationObservation.Failure(ex, logger, diagnosticIds, "search", account.AccountId, account.AgentInstanceId);
            observation.Fail(failure); throw failure;
        }
    }
    private async ValueTask<EmailSearchResult> SearchCoreAsync(EmailSearchRequest request, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(request.Limit, 1, EmailToolLimits.MaxSearchLimit);
        var query = string.IsNullOrWhiteSpace(request.Query) ? "in:inbox" : request.Query;
        using var list = await Get("messages?maxResults=" + limit + "&q=" + Uri.EscapeDataString(query), cancellationToken);
        var results = new List<EmailSearchItem>();
        if (list.RootElement.TryGetProperty("messages", out var messages))
        foreach (var item in messages.EnumerateArray().Take(limit))
        {
            var id = item.GetProperty("id").GetString()!;
            using var json = await Get("messages/" + Uri.EscapeDataString(id) + "?format=metadata&metadataHeaders=Subject&metadataHeaders=From&metadataHeaders=Date", cancellationToken);
            var root = json.RootElement; var headers = root.GetProperty("payload").GetProperty("headers");
            results.Add(new(Prefix + id, root.TryGetProperty("threadId", out var thread) ? Prefix + thread.GetString() : "",
                ReadHeader(headers, "From") ?? "", ReadHeader(headers, "Subject") ?? "",
                DateTimeOffset.TryParse(ReadHeader(headers, "Date"), out var date) ? date : time.GetUtcNow(),
                root.TryGetProperty("snippet", out var snippet) ? snippet.GetString() ?? "" : ""));
        }
        return new(results, list.RootElement.TryGetProperty("nextPageToken", out _));
    }
    public async ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken cancellationToken = default)
    {
        using var observation = new EmailOperationObservation("read", account.Profile.Synthetic, time, account.AccountId, cancellationToken);
        try { var result = await ReadCoreAsync(request, cancellationToken); observation.Complete(); return result; }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested) { observation.Fail(ex); throw; }
        catch (Exception ex)
        {
            var failure = EmailOperationObservation.Failure(ex, logger, diagnosticIds, "read", account.AccountId, account.AgentInstanceId);
            observation.Fail(failure); throw failure;
        }
    }
    private async ValueTask<EmailMessageResult> ReadCoreAsync(EmailReadRequest request, CancellationToken cancellationToken)
    {
        if (!request.MessageId.StartsWith(Prefix, StringComparison.Ordinal) || request.MessageId.Length > 512)
            throw AgentCoreErrors.NotFound("Message reference is not in this account.");
        using var json = await Get("messages/" + Uri.EscapeDataString(request.MessageId[Prefix.Length..]) + "?format=full", cancellationToken);
        var root = json.RootElement; var payload = root.GetProperty("payload"); var headers = payload.GetProperty("headers");
        var body = ExtractBody(root); var truncated = body.Length > EmailToolLimits.MaxBodyLength;
        return new(request.MessageId, root.TryGetProperty("threadId", out var thread) ? Prefix + thread.GetString() : "",
            ReadHeader(headers, "From") ?? "", SplitAddresses(ReadHeader(headers, "To")), SplitAddresses(ReadHeader(headers, "Cc")),
            DateTimeOffset.TryParse(ReadHeader(headers, "Date"), out var date) ? date : time.GetUtcNow(), ReadHeader(headers, "Subject") ?? "",
            truncated ? body[..EmailToolLimits.MaxBodyLength] : body, truncated, []);
    }
    internal async Task<JsonDocument> Get(string path, CancellationToken ct)
    {
        await recheck(ct); var access = await token(ct); await recheck(ct);
        using var client = clients.CreateClient(HttpClientName);
        HttpRequestMessage Request(string value)
        { var request = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/" + path); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value); return request; }
        var response = await SendBoundedAsync(client, () => Request(access), true, time, ct, recheck);
        try
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose(); access = await (freshToken ?? token)(ct); await recheck(ct);
                response = await SendBoundedAsync(client, () => Request(access), true, time, ct, recheck);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (rejectAuthorization is not null) await rejectAuthorization(ct);
                    throw new AgentCoreException("email_oauth_reconnect_required", "Google rejected current authorization. Reconnect in Connections → Credentials.", 403);
                }
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Google mailbox is unavailable.", null, response.StatusCode);
            var json = await ReadJson(response, 2 * 1024 * 1024, ct);
            try { await recheck(ct); return json; } catch { json.Dispose(); throw; }
        }
        finally { response.Dispose(); }
    }
    public ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException("Core owns drafts.");
    public ValueTask<EmailDraftSnapshot?> GetDraftAsync(string draftId, CancellationToken cancellationToken = default) => throw new NotSupportedException("Core owns drafts.");
    public async ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest request, CancellationToken cancellationToken = default)
    {
        using var observation = new EmailOperationObservation("send", account.Profile.Synthetic, time, account.AccountId, cancellationToken);
        var draft = request.ApprovedDraft;
        if (request.DraftId != draft.DraftId || draft.AccountId != account.AccountId || draft.Sender != account.Profile.EmailAddress
            || draft.AccountRevision != account.AccountRevision || draft.AuthEpoch != account.Profile.AuthEpoch)
            throw AgentCoreErrors.Forbidden("Draft identity or authorization does not match the approved Gmail account.");
        using var message = GmailMime.BuildMessage(draft.To, draft.Cc, draft.Bcc, draft.Subject, draft.Body);
        message.From.Add(MailboxAddress.Parse(account.Profile.EmailAddress));
        using var stream = new MemoryStream(); message.WriteTo(stream);
        var raw = GmailMime.EncodeBase64Url(stream.ToArray());
        if (preparedToken is null) await PrepareSendAsync(cancellationToken);
        await recheck(cancellationToken);
        if (!sendEnabled()) return new(EmailSendOutcome.Failed, null, "email_send_disabled", "Sending was disabled before dispatch.");
        using var client = clients.CreateClient(HttpClientName);
        using var outbound = new HttpRequestMessage(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/messages/send")
        { Content = new StringContent(JsonSerializer.Serialize(new { raw }), Encoding.UTF8, "application/json") };
        outbound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", preparedToken);
        try
        {
            using var response = await client.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) HandledFailureLog.Warning(logger, new HttpRequestException("Google rejected email dispatch.", null, response.StatusCode),
                "email.send", (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout ? "email_send_indeterminate" : "email_send_rejected",
                new DiagnosticContext(ProviderAlias: "gmail", AgentInstanceId: account.AgentInstanceId, CredentialId: account.AccountId), diagnostics: diagnosticIds);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
            {
                observation.Complete("indeterminate");
                return new(EmailSendOutcome.Indeterminate, null, "email_send_indeterminate", "Google acceptance could not be confirmed. This draft will not be retried.");
            }
            if (!response.IsSuccessStatusCode) { observation.Complete("failed"); return new(EmailSendOutcome.Failed, null, "email_send_rejected", "Google rejected the message."); }
            using var json = await ReadJson(response, 64 * 1024, cancellationToken);
            if (json.RootElement.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } accepted)
            { observation.Complete(); return new(EmailSendOutcome.Sent, accepted, null, null); }
            observation.Complete("indeterminate");
            HandledFailureLog.Warning(logger, new InvalidDataException("Google response did not include a message ID."), "email.send", "email_send_indeterminate",
                new DiagnosticContext(ProviderAlias: "gmail", AgentInstanceId: account.AgentInstanceId, CredentialId: account.AccountId, FailureReason: "missing_message_id"), diagnostics: diagnosticIds);
            return new(EmailSendOutcome.Indeterminate, null, "email_send_indeterminate", "Google response did not confirm a message ID. This draft will not be retried.");
        }
        catch (Exception ex)
        {
            observation.Complete("indeterminate");
            HandledFailureLog.Warning(logger, ex, "email.send", "dispatch_unconfirmed", new DiagnosticContext(ProviderAlias: "gmail", AgentInstanceId: account.AgentInstanceId, CredentialId: account.AccountId), diagnostics: diagnosticIds);
            return new(EmailSendOutcome.Indeterminate, null, "email_send_indeterminate", "Google acceptance could not be confirmed. This draft will not be retried.");
        }
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

    private static IReadOnlyList<string> SplitAddresses(string? value) => InternetAddressList.TryParse(value ?? "", out var addresses)
        ? addresses.Mailboxes.Select(m => m.Address).ToArray() : [];

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
        if (builder.Length > EmailToolLimits.MaxBodyLength) return;
        if (payload.TryGetProperty("mimeType", out var mime) && mime.GetString() == "text/plain"
            && payload.TryGetProperty("body", out var body)
            && body.TryGetProperty("data", out var dataNode)
            && dataNode.GetString() is { Length: > 0 } data)
        {
            try
            {
                builder.Append(Encoding.UTF8.GetString(GmailMime.DecodeBase64Url(data)));
            }
            catch (FormatException)
            {
            }
        }

        if (payload.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                CollectBody(part, builder);
            }
        }
    }

    private static async Task<HttpResponseMessage> SendBoundedAsync(HttpClient client, Func<HttpRequestMessage> request, bool retrySafe, TimeProvider time, CancellationToken ct,
        Func<CancellationToken, ValueTask>? recheck = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (recheck is not null) await recheck(ct);
            using var outgoing = request();
            try
            {
                var response = await client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!retrySafe || attempt >= 1 || (int)response.StatusCode != 429 && (int)response.StatusCode < 500) return response;
                response.Dispose();
            }
            catch (HttpRequestException) when (retrySafe && attempt < 1) { }
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }
    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw new AgentCoreException("email_provider_response_invalid", "Google response exceeded the safe limit.", 502);
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var count = await input.ReadAsync(buffer, ct); if (count == 0) break; if (output.Length + count > limit) throw new AgentCoreException("email_provider_response_invalid", "Google response exceeded the safe limit.", 502); output.Write(buffer, 0, count); }
        return JsonDocument.Parse(output.ToArray());
    }

}
