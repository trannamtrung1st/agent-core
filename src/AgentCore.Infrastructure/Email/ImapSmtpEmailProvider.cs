using System.Net.Sockets;
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
    Func<EmailAccountContext, CancellationToken, ValueTask<string>> secret) : IEmailProvider
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
        var endpoint = smtp ? account.Profile.SmtpEndpoint : account.Profile.ImapEndpoint;
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
        await connections.WaitAsync(ct);
        try
        {
            using var client = new ImapClient(); await AuthenticateAsync(client, false, ct);
            var inbox = client.Inbox; await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            if (inbox.Count == 0) return new([], false);
            var next = inbox.UidNext?.Id ?? uint.MaxValue;
            var first = Math.Max(1, next > 500 ? next - 500 : 1);
            SearchQuery query = SearchQuery.Uids(new UniqueIdRange(new UniqueId(first), UniqueId.MaxValue));
            if (request.Query.Length > 0) query = query.And(SearchQuery.SubjectContains(request.Query)
                .Or(SearchQuery.FromContains(request.Query)).Or(SearchQuery.BodyContains(request.Query)));
            var matches = await inbox.SearchAsync(query, ct);
            var limit = Math.Clamp(request.Limit, 1, EmailToolLimits.MaxSearchLimit);
            var ids = matches.Reverse().Take(limit).ToArray();
            if (ids.Length == 0) return new([], matches.Count > limit || first > 1);
            var summaries = await inbox.FetchAsync(ids, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate, ct);
            return new(summaries.OrderByDescending(s => s.UniqueId.Id).Select(s => new EmailSearchItem(
                Reference(inbox.UidValidity, s.UniqueId.Id), "", Bound(s.Envelope?.From.ToString() ?? "", 512), Bound(s.Envelope?.Subject ?? "", 512),
                s.InternalDate ?? DateTimeOffset.UnixEpoch, "")).ToArray(), matches.Count > limit || first > 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AgentCoreException) { throw; }
        catch (Exception) { throw AgentCoreErrors.Validation("mail_read_failed: Mailbox TLS/authentication or bounded search failed."); }
        finally { connections.Release(); }
    }
    private string Reference(uint validity, uint uid) => $"imap-{account.AccountId:N}:{validity}:{uid}";
    public async ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken cancellationToken = default)
    {
        var parts = request.MessageId.Split(':');
        if (parts.Length != 3 || parts[0] != $"imap-{account.AccountId:N}" || !uint.TryParse(parts[1], out var validity)
            || !uint.TryParse(parts[2], out var uid) || uid == 0) throw AgentCoreErrors.NotFound("Message reference is not in this account.");
        using var deadline = Deadline(cancellationToken); var ct = deadline.Token;
        await connections.WaitAsync(ct);
        try
        {
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
        catch (Exception) { throw AgentCoreErrors.Validation("mail_read_failed: Message could not be safely read within mail limits."); }
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
        catch (SmtpCommandException) { return new(EmailSendOutcome.Failed, null, "smtp_rejected", "SMTP server rejected this dispatch. Create a new approved draft for an intentional retry."); }
        catch (Exception)
        {
            return attempted ? new(EmailSendOutcome.Indeterminate, null, "dispatch_unconfirmed", "Delivery could not be confirmed. The message may have been sent; it will not be retried automatically.")
                : new(EmailSendOutcome.Failed, null, "mail_authentication_failed", "Mail authorization, TLS, connection or authentication failed before dispatch.");
        }
        finally { if (acquired) connections.Release(); }
    }
    private static string Bound(string value, int limit) => value.Length <= limit ? value : value[..limit];
}
