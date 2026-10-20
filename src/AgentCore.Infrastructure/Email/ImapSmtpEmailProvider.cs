using System.Net.Sockets;
using AgentCore.Application.Observability;
using Microsoft.Extensions.Logging;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Credentials;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace AgentCore.Infrastructure.Email;

public sealed class ImapSmtpEmailProvider(EmailAccountContext account, EmailTransportOptions options,
    MailTransportPolicy policy, SemaphoreSlim connections,
    Func<EmailAccountContext, CancellationToken, ValueTask<string>> secret, ILogger<ImapSmtpEmailProvider>? logger = null, IDiagnosticIdSource? diagnosticIds = null) : IEmailProvider
{
    internal Func<CancellationToken, ValueTask<Socket>>? SocketFactory { get; init; }
    internal Action<MailService>? ConfigureClient { get; init; }
    private const int MaxMessageBytes = 512 * 1024;
    public bool IsAvailable => options.Enabled;
    private CancellationTokenSource Deadline(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60)));
        return source;
    }
    private static SecureSocketOptions Tls(MailEndpoint endpoint) => endpoint.TlsMode == MailTlsMode.ImplicitTls
        ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
    private async Task AuthenticateAsync(MailService client, bool smtp, CancellationToken ct)
    {
        var endpoint = smtp ? account.Profile.SmtpEndpoint! : account.Profile.ImapEndpoint!;
        policy.ValidateEndpoint(account.Profile, smtp);
        var socket = SocketFactory is null ? await policy.ConnectAsync(account.Profile, smtp, ct) : await SocketFactory(ct);
        ConfigureClient?.Invoke(client);
        // No protocol logger, proxy, redirects or custom certificate acceptance callback.
        client.Timeout = Math.Clamp(options.TimeoutSeconds, 1, 60) * 1000;
        client.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
        try { await client.ConnectAsync(socket, endpoint.Host, endpoint.Port, Tls(endpoint), ct); }
        catch { socket.Dispose(); throw; }
        var password = await secret(account, ct);
        await client.AuthenticateAsync(smtp ? account.Profile.SmtpUsername : account.Profile.ImapUsername, password, ct);
        // Socket ownership transfers to MailKit; disposing the local Socket would close the connected session.
    }
    public async ValueTask TestAsync(bool smtp, CancellationToken ct)
    {
        using var deadline = Deadline(ct); ct = deadline.Token;
        await connections.WaitAsync(ct);
        try
        {
            using MailService client = smtp ? new SmtpClient() : new ImapClient();
            await AuthenticateAsync(client, smtp, ct);
        }
        finally { connections.Release(); }
    }
    public async ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest request, CancellationToken cancellationToken = default)
    {
        using var deadline = Deadline(cancellationToken); var ct = deadline.Token;
        var acquired = false;
        try
        {
            await connections.WaitAsync(ct); acquired = true;
            using var client = new ImapClient(); await AuthenticateAsync(client, false, ct);
            var inbox = client.Inbox; await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            if (inbox.Count == 0) return new([], false);
            SearchQuery query;
            bool scanTruncated;
            if (inbox.UidNext is { } next)
            {
                var first = Math.Max(1, next.Id > 500 ? next.Id - 500 : 1);
                query = SearchQuery.Uids(new UniqueIdRange(new UniqueId(first), UniqueId.MaxValue));
                scanTruncated = first > 1;
            }
            else
            {
                // Sequence indexes bound the fallback even with sparse or unknown UIDs.
                var firstIndex = Math.Max(0, inbox.Count - 500);
                var recent = await inbox.FetchAsync(firstIndex, inbox.Count - 1, MessageSummaryItems.UniqueId, ct);
                var recentIds = recent.Select(s => s.UniqueId).Where(id => id.IsValid).ToArray();
                if (recentIds.Length == 0) return new([], firstIndex > 0);
                query = SearchQuery.Uids(recentIds);
                scanTruncated = firstIndex > 0;
            }
            if (request.Query.Length > 0) query = query.And(SearchQuery.SubjectContains(request.Query)
                .Or(SearchQuery.FromContains(request.Query)).Or(SearchQuery.BodyContains(request.Query)));
            var matches = await inbox.SearchAsync(query, ct);
            var limit = Math.Clamp(request.Limit, 1, EmailToolLimits.MaxSearchLimit);
            var ids = matches.Reverse().Take(limit).ToArray();
            if (ids.Length == 0) return new([], matches.Count > limit || scanTruncated);
            var summaries = await inbox.FetchAsync(ids, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate, ct);
            return new(summaries.OrderByDescending(s => s.UniqueId.Id).Select(s => new EmailSearchItem(
                Reference(inbox.UidValidity, s.UniqueId.Id), "", Bound(s.Envelope?.From.ToString() ?? "", 512), Bound(s.Envelope?.Subject ?? "", 512),
                s.InternalDate ?? DateTimeOffset.UnixEpoch, "")).ToArray(), matches.Count > limit || scanTruncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AgentCoreException) { throw; }
        catch (Exception ex)
        {
            var id = LogFailure(ex, "email.search", "mail_read_failed");
            throw new AgentCoreException("ValidationError", "mail_read_failed: Mailbox TLS/authentication or bounded search failed.", 400) { DiagnosticId = id };
        }
        finally { if (acquired) connections.Release(); }
    }
    private string Reference(uint validity, uint uid) => EmailMessageReferences.Imap(account.AccountId, validity, uid);
    public async ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken cancellationToken = default)
    {
        var parts = request.MessageId.Split(':');
        if (parts.Length != 3 || parts[0] != $"imap-{account.AccountId:N}" || !uint.TryParse(parts[1], out var validity)
            || !uint.TryParse(parts[2], out var uid) || uid == 0) throw AgentCoreErrors.NotFound("Message reference is not in this account.");
        using var deadline = Deadline(cancellationToken); var ct = deadline.Token;
        var acquired = false;
        try
        {
            await connections.WaitAsync(ct); acquired = true;
            using var client = new ImapClient(); await AuthenticateAsync(client, false, ct);
            var inbox = client.Inbox; await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            if (inbox.UidValidity != validity) throw AgentCoreErrors.NotFound("Mailbox identity changed. Search again for a fresh message reference.");
            // Partial BODY.PEEK stream bounds transferred bytes and never sets Seen.
            using var stream = await inbox.GetStreamAsync(new UniqueId(uid), 0, MaxMessageBytes, ct);
            using var message = await MimeMessage.LoadAsync(stream, ct);
            var body = message.TextBody ?? (message.HtmlBody is null ? "" : System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(message.HtmlBody, "<[^>]*>", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100))));
            var truncated = stream.Length >= MaxMessageBytes || body.Length > EmailToolLimits.MaxBodyLength;
            return new(request.MessageId, "", Bound(message.From.ToString(), 512), message.To.Mailboxes.Take(25).Select(m => m.Address).ToArray(),
                message.Cc.Mailboxes.Take(25).Select(m => m.Address).ToArray(), message.Date, Bound(message.Subject ?? "", 512),
                Bound(body, EmailToolLimits.MaxBodyLength), truncated, []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AgentCoreException) { throw; }
        catch (Exception ex)
        {
            var id = LogFailure(ex, "email.read", "mail_read_failed");
            throw new AgentCoreException("ValidationError", "mail_read_failed: Message could not be safely read within mail limits.", 400) { DiagnosticId = id };
        }
        finally { if (acquired) connections.Release(); }
    }
    // Uses the exact same TLS, pinned DNS, timeout and connection ceiling as reads.
    internal async ValueTask<EmailWatchBatch> WatchAsync(AgentCore.Domain.Email.EmailWatchCursor? cursor, CancellationToken cancellationToken, Func<CancellationToken, ValueTask>? recheck = null)
    {
        using var deadline = Deadline(cancellationToken); var ct = deadline.Token;
        await connections.WaitAsync(ct);
        try
        {
            if (recheck is not null) await recheck(ct);
            using var client = new ImapClient(); await AuthenticateAsync(client, false, ct);
            if (recheck is not null) await recheck(ct);
            var inbox = client.Inbox; await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            async Task<uint> Latest()
            {
                if (inbox.UidNext is { } next) return next.Id - 1;
                if (inbox.Count == 0) return 0;
                if (recheck is not null) await recheck(ct);
                var last = await inbox.FetchAsync(inbox.Count - 1, inbox.Count - 1, MessageSummaryItems.UniqueId, ct);
                return last.Single().UniqueId.Id;
            }
            AgentCore.Domain.Email.EmailWatchCursor Position(uint uid) => new(AgentCore.Domain.Email.EmailWatchCursorKind.ImapUid, UidValidity: inbox.UidValidity, LastUid: uid);
            if (cursor is null) return new([], Position(await Latest()));
            if (cursor.Kind != AgentCore.Domain.Email.EmailWatchCursorKind.ImapUid || cursor.UidValidity != inbox.UidValidity)
                return new([], Position(await Latest()), CursorGap: true);
            if (cursor.LastUid == uint.MaxValue) return new([], cursor);
            if (inbox.Count == 0) return new([], Position(Math.Max(cursor.LastUid, await Latest())));
            if (inbox.UidNext is null)
            {
                // Sequence fallback inspects UID identity only. A bounded scan must cover the
                // committed UID; otherwise explicitly disclose the gap and baseline from now.
                var first = Math.Max(0, inbox.Count - 500);
                if (recheck is not null) await recheck(ct);
                var recent = await inbox.FetchAsync(first, inbox.Count - 1, MessageSummaryItems.UniqueId, ct);
                var uids = recent.Select(m => m.UniqueId.Id).Order().ToArray();
                if (first > 0 && (uids.Length == 0 || uids[0] > cursor.LastUid)) return new([], Position(await Latest()), CursorGap: true);
                var newUids = uids.Where(uid => uid > cursor.LastUid).Take(25).ToArray();
                return new(newUids.Select(uid => Reference(inbox.UidValidity, uid)).ToArray(),
                    Position(newUids.LastOrDefault(cursor.LastUid)), uids.Count(uid => uid > cursor.LastUid) > 25);
            }
            var ceiling = inbox.UidNext.Value.Id - 1;
            if (ceiling <= cursor.LastUid) return new([], cursor);
            // Bound by UID range, not recent inbox search; sparse/deleted UIDs still advance.
            var end = (uint)Math.Min(ceiling, (ulong)cursor.LastUid + 500);
            if (recheck is not null) await recheck(ct);
            var matches = await inbox.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId(cursor.LastUid + 1), new UniqueId(end))), ct);
            var ids = matches.OrderBy(u => u.Id).Take(25).ToArray();
            var lastUid = matches.Count > 25 ? ids[^1].Id : end;
            return new(ids.Select(u => Reference(inbox.UidValidity, u.Id)).ToArray(), Position(lastUid), lastUid < ceiling);
        }
        finally { connections.Release(); }
    }
    public ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException("Core owns drafts.");
    public ValueTask<EmailDraftSnapshot?> GetDraftAsync(string draftId, CancellationToken cancellationToken = default) => throw new NotSupportedException("Core owns drafts.");
    public async ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest request, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || !options.SmtpEnabled) return new(EmailSendOutcome.Failed, null, "smtp_disabled", "Operator must enable SMTP after verification.");
        var d = request.ApprovedDraft;
        if (d.AccountId != account.AccountId || d.Sender != account.Profile.EmailAddress || request.DraftId != d.DraftId
            || !EmailHeaderSafety.IsSafeSubject(d.Subject) || !EmailHeaderSafety.IsSafeBody(d.Body)
            || d.To.Count == 0 || d.To.Concat(d.Cc).Concat(d.Bcc).Any(a => !EmailHeaderSafety.TryValidateAddress(a, out _)))
            return new(EmailSendOutcome.Failed, null, "invalid", "Draft headers or account identity are invalid.");
        using var deadline = Deadline(cancellationToken); var ct = deadline.Token;
        var attempted = false; var acquired = false;
        try
        {
            await connections.WaitAsync(ct); acquired = true;
            using var client = new SmtpClient(); await AuthenticateAsync(client, true, ct);
            using var message = new MimeMessage();
            var sender = MailboxAddress.Parse(account.Profile.EmailAddress);
            message.From.Add(sender); foreach (var a in d.To) message.To.Add(MailboxAddress.Parse(a));
            foreach (var a in d.Cc) message.Cc.Add(MailboxAddress.Parse(a));
            message.Subject = d.Subject; message.Body = new TextPart("plain") { Text = d.Body };
            message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
            var recipients = d.To.Concat(d.Cc).Concat(d.Bcc).Distinct(StringComparer.OrdinalIgnoreCase).Select(MailboxAddress.Parse).ToArray();
            await secret(account, ct); // Live revocation check immediately before dispatch.
            ct.ThrowIfCancellationRequested(); attempted = true;
            await client.SendAsync(message, sender, recipients, ct);
            return new(EmailSendOutcome.Sent, message.MessageId, null, null);
        }
        catch (SmtpCommandException ex)
        {
            LogFailure(ex, "email.send", "smtp_rejected", ex.StatusCode.ToString());
            return new(EmailSendOutcome.Failed, null, "smtp_rejected", "SMTP server rejected this dispatch. Create a new approved draft for an intentional retry.");
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested || attempted)
                LogFailure(ex, "email.send", attempted ? "dispatch_unconfirmed" : "mail_authentication_failed");
            return attempted ? new(EmailSendOutcome.Indeterminate, null, "dispatch_unconfirmed", "Delivery could not be confirmed. The message may have been sent; it will not be retried automatically.")
                : new(EmailSendOutcome.Failed, null, "mail_authentication_failed", "Mail authorization, TLS, connection or authentication failed before dispatch.");
        }
        finally { if (acquired) connections.Release(); }
    }
    private Guid? LogFailure(Exception ex, string operation, string code, string? reason = null) =>
        HandledFailureLog.Warning(logger, ex, operation, code,
            new DiagnosticContext(AgentInstanceId: account.AgentInstanceId, ProviderAlias: "imap-smtp", FailureReason: reason, CredentialId: account.AccountId), diagnostics: diagnosticIds);

    private static string Bound(string value, int limit) => value.Length <= limit ? value : value[..limit];
}
