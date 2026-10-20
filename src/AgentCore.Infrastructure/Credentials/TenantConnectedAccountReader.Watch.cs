using System.Collections.Concurrent;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Email;
using AgentCore.Infrastructure.Email;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Credentials;

public sealed partial class TenantConnectedAccountReader
{
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Credential), List<string>> watchArrivals = new();
    bool ITenantEmailChangeFeed.IsSynthetic => synthetic;
    bool ITenantEmailChangeFeed.HostAvailable => emailHost.Enabled;
    public ConnectedAccountAvailability WatchAvailability(Credential account) => account.Purpose == CredentialPurpose.EmailAccount
        ? Availability(account) : new(false, "Unavailable");
    internal void AddTenantSyntheticArrival(Guid tenant, Guid account, EmailMessageResult message)
    {
        if (!synthetic || tenant == Guid.Empty || account == Guid.Empty) throw new InvalidOperationException("Synthetic fixture only.");
        mailboxes.GetOrAdd((tenant, account), _ => new()).AddMessage(message);
        var arrivals = watchArrivals.GetOrAdd((tenant, account), _ => []); lock (arrivals) arrivals.Add(message.MessageId);
    }
    public async ValueTask<EmailWatchBatch> ReadTenantChangesAsync(Guid tenantId, Credential account, EmailWatchCursor? cursor,
        Func<CancellationToken, ValueTask<Credential>> recheck, Func<CredentialOAuthCheck, CancellationToken, ValueTask> maintenance, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || account.Purpose != CredentialPurpose.EmailAccount) throw new AccessDeniedException("email_watch_source_unavailable");
        async ValueTask<Credential> Current(CancellationToken token)
        {
            var fresh = await recheck(token).ConfigureAwait(false);
            if (fresh.CredentialId != account.CredentialId || fresh.Purpose != CredentialPurpose.EmailAccount || !WatchAvailability(fresh).Available)
                throw new AccessDeniedException("email_watch_source_unavailable");
            return fresh;
        }
        var current = await Current(ct).ConfigureAwait(false); EmailWatchBatch batch;
        if (synthetic)
        {
            var arrivals = watchArrivals.GetOrAdd((tenantId, current.CredentialId), _ => []);
            lock (arrivals)
            {
                if (cursor is null || cursor.Kind != EmailWatchCursorKind.Synthetic || cursor.Offset > arrivals.Count)
                    batch = new([], new(EmailWatchCursorKind.Synthetic, Offset: arrivals.Count), CursorGap: cursor is not null);
                else
                {
                    var references = arrivals.Skip(cursor.Offset).Take(25).Select(id => $"syn-{current.CredentialId:N}:{id}").ToArray();
                    batch = new(references, cursor with { Offset = cursor.Offset + references.Length }, HasMore: cursor.Offset + references.Length < arrivals.Count);
                }
            }
        }
        else if (current.EmailAccount!.AuthMethod == EmailAuthMethod.GoogleOAuth2)
        {
            string? access = null;
            async ValueTask<string> Token(CancellationToken token, bool fresh = false)
            {
                await Current(token).ConfigureAwait(false);
                if (access is null || fresh) access = await google.RuntimeTokenAsync(Current, maintenance, token).ConfigureAwait(false);
                await Current(token).ConfigureAwait(false); return access;
            }
            // A source poll has an account identity, not a fabricated executing
            // Instance/binding. The callback supplies its exact live watch fence.
            var context = new EmailAccountContext(Guid.Empty, current.CredentialId, "", current.DisplayName, current.Revision, Guid.Empty, 0, current.EmailAccount);
            var mailbox = new GmailEmailProvider(context, clients, token => Token(token), async token => { await Current(token).ConfigureAwait(false); },
                () => false, time, logs.CreateLogger<GmailEmailProvider>(), diagnostics, token => Token(token, true),
                async token => { await Current(token).ConfigureAwait(false); await maintenance(new(ReconnectRequired: true), token).ConfigureAwait(false); });
            var gmail = new GmailHistoryChangeFeed(current.CredentialId, mailbox);
            batch = cursor is null ? new([], await gmail.BaselineAsync(ct).ConfigureAwait(false)) : await gmail.PollAsync(cursor, ct).ConfigureAwait(false);
        }
        else
        {
            var context = new EmailAccountContext(Guid.Empty, current.CredentialId, "", current.DisplayName, current.Revision, Guid.Empty, 0, current.EmailAccount!);
            var mailbox = new ImapSmtpEmailProvider(context, emailHost, new MailTransportPolicy(emailHost), mailConnections,
                async (_, token) => { var fresh = await Current(token).ConfigureAwait(false); return protector.Unprotect(fresh.CredentialId, fresh.ProtectionVersion, fresh.ProtectedPayload); },
                logs.CreateLogger<ImapSmtpEmailProvider>(), diagnostics);
            try { batch = await mailbox.WatchAsync(cursor, ct, async token => { await Current(token).ConfigureAwait(false); }).ConfigureAwait(false); }
            catch (Exception ex) when (ex is MailKit.Security.AuthenticationException or MailKit.Security.SslHandshakeException or System.Security.Authentication.AuthenticationException
                or AgentCore.Application.Sessions.AgentCoreException { StatusCode: 400 or 403 })
            { throw new AgentCore.Application.Email.MailAuthorizationUnavailableException(); }
        }
        await Current(ct).ConfigureAwait(false); TenantEmailWatchRules.Batch(batch); return batch;
    }
}
