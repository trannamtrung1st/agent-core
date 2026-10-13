using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Email;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Email;

public sealed class AccountEmailChangeFeed(ICredentialStore credentials, ICredentialProtector protector,
    EmailTransportOptions options, GoogleEmailOAuthService oauth, IHttpClientFactory clients, TimeProvider time,
    AccountEmailProviderFactory providers, bool synthetic) : IEmailChangeFeed
{
    private async ValueTask Recheck(Credential account, CancellationToken ct)
    {
        var current = await credentials.GetAsync(account.CredentialId, ct);
        if (!options.Enabled || current is null || !EmailWatchTransitions.Usable(current) || EmailWatchTransitions.Fingerprint(current) != EmailWatchTransitions.Fingerprint(account)
            || current.EmailAccount?.AuthMethod == EmailAuthMethod.GoogleOAuth2 && current.EmailAccount.Synthetic != synthetic)
            throw AgentCoreErrors.Forbidden("mail_authority_revoked: Reconnect or review the email account.");
    }
    private GmailHistoryChangeFeed Gmail(Credential c)
    {
        var context = new EmailAccountContext(Guid.Empty, c.CredentialId, "", c.DisplayName, c.Revision, Guid.Empty, 0, c.EmailAccount!);
        return new(c.CredentialId, new(context, clients, ct => oauth.AccessTokenAsync(c.CredentialId, c.Revision, ct),
            ct => Recheck(c, ct), () => false, time, freshToken: ct => oauth.AccessTokenAsync(c.CredentialId, c.Revision, ct, forceRefresh: true),
            rejectAuthorization: ct => oauth.RejectAuthorizationAsync(c.CredentialId, c.Revision, ct)));
    }
    private ImapSmtpEmailProvider Imap(Credential c) => providers.CreateWatcher(c, async ct =>
    { await Recheck(c, ct); var current = (await credentials.GetAsync(c.CredentialId, ct))!; return protector.Unprotect(c.CredentialId, current.ProtectionVersion, current.ProtectedPayload); });
    private async ValueTask<EmailWatchBatch> Watch(Credential c, EmailWatchCursor? cursor, CancellationToken ct)
    {
        try { return await Imap(c).WatchAsync(cursor, ct); }
        catch (Exception ex) when (ex is MailKit.Security.AuthenticationException or MailKit.Security.SslHandshakeException or System.Security.Authentication.AuthenticationException
            || ex is AgentCoreException { StatusCode: 400 or 403 }) { throw new AgentCore.Application.Email.MailAuthorizationUnavailableException(); }
    }
    public async ValueTask<EmailWatchCursor> BaselineAsync(Credential c, CancellationToken ct)
    {
        await Recheck(c, ct);
        var cursor = synthetic ? providers.SyntheticBaseline(c.CredentialId)
            : c.EmailAccount!.Preset == EmailProviderPreset.Gmail ? await Gmail(c).BaselineAsync(ct) : (await Watch(c, null, ct)).NextCursor;
        await Recheck(c, ct); return cursor;
    }
    public async ValueTask<EmailWatchBatch> PollAsync(Credential c, EmailWatchCursor cursor, CancellationToken ct)
    {
        await Recheck(c, ct);
        var batch = synthetic ? providers.SyntheticPoll(c.CredentialId, cursor)
            : c.EmailAccount!.Preset == EmailProviderPreset.Gmail ? await Gmail(c).PollAsync(cursor, ct) : await Watch(c, cursor, ct);
        await Recheck(c, ct); return batch;
    }
}
