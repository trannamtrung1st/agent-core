using System.Collections.Concurrent;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Email;

public sealed class AccountEmailProviderFactory(ICredentialStore credentials, IAgentCredentialBindingStore bindings,
    ICredentialProtector protector, IAgentInstanceStore instances, EmailTransportOptions options, TimeProvider time,
    bool synthetic) : IEmailProviderFactory
{
    private readonly ConcurrentDictionary<Guid, SyntheticEmailProvider> _synthetic = new();
    private readonly SemaphoreSlim _connections = new(4);
    private readonly MailTransportPolicy _policy = new(options);
    public bool IsEnabled => options.Enabled;
    public IEmailProvider Create(EmailAccountContext account) => synthetic
        ? new ScopedSyntheticEmailProvider(account, _synthetic.GetOrAdd(account.AccountId, _ => new()), RecheckAsync)
        : new ImapSmtpEmailProvider(account, options, _policy, _connections, ResolveSecretAsync);

    private async ValueTask RecheckAsync(EmailAccountContext account, CancellationToken ct)
    {
        if (await instances.FindAsync(account.AgentInstanceId, ct) is not { Lifecycle: AgentInstanceLifecycle.Active }
            || !(await bindings.ListBindingsAsync(account.AgentInstanceId, ct)).Any(b => b.BindingId == account.BindingId
                && b.CredentialId == account.AccountId && b.Reference == account.AccountRef && b.Revision == account.BindingRevision))
            throw AgentCoreErrors.Forbidden("mail_authority_revoked: Account binding is unavailable.");
        await RequireCredential(account.AccountId, account.AccountRevision, ct);
    }
    private async ValueTask<Credential> RequireCredential(Guid id, long revision, CancellationToken ct)
    {
        var credential = await credentials.GetAsync(id, ct);
        if (credential is not { Purpose: CredentialPurpose.EmailAccount, Kind: CredentialKind.Password, Status: CredentialStatus.Active, EmailAccount: not null }
            || credential.Revision != revision) throw AgentCoreErrors.Forbidden("mail_authority_revoked: Email account changed or is disabled.");
        return credential;
    }
    private async ValueTask<string> ResolveSecretAsync(EmailAccountContext account, CancellationToken ct)
    {
        await RecheckAsync(account, ct);
        var credential = await RequireCredential(account.AccountId, account.AccountRevision, ct);
        return protector.Unprotect(credential.CredentialId, credential.ProtectionVersion, credential.ProtectedPayload);
    }
    public async ValueTask<EmailConnectionTest> TestAsync(Guid accountId, long expectedRevision, string protocol, CancellationToken ct = default)
    {
        if (protocol is not ("imap" or "smtp")) throw AgentCoreErrors.Validation("Protocol must be imap or smtp.");
        var credential = await RequireCredential(accountId, expectedRevision, ct);
        if (!options.Enabled) return new(protocol, "disabled", time.GetUtcNow(), "Email transport is disabled.");
        if (synthetic) return new(protocol, "synthetic", time.GetUtcNow(), null);
        var profile = credential.EmailAccount!;
        var account = new EmailAccountContext(Guid.Empty, accountId, "", credential.DisplayName, expectedRevision, Guid.Empty, 0, profile);
        var provider = new ImapSmtpEmailProvider(account, options, _policy, _connections, async (_, token) =>
        {
            var current = await RequireCredential(accountId, expectedRevision, token);
            return protector.Unprotect(current.CredentialId, current.ProtectionVersion, current.ProtectedPayload);
        });
        try { await provider.TestAsync(protocol == "smtp", ct); return new(protocol, "connected", time.GetUtcNow(), null); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(protocol, "failed", time.GetUtcNow(), "TLS/authentication or endpoint validation failed. Check the account and operator allowlist, then retry."); }
    }
}
internal sealed class ScopedSyntheticEmailProvider(EmailAccountContext account, SyntheticEmailProvider inner,
    Func<EmailAccountContext, CancellationToken, ValueTask> recheck) : IEmailProvider
{
    public bool IsAvailable => true;
    private string Prefix => "syn-" + account.AccountId.ToString("N") + ":";
    public async ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest request, CancellationToken cancellationToken = default)
    {
        await recheck(account, cancellationToken);
        var result = await inner.SearchAsync(request, cancellationToken);
        return result with { Results = result.Results.Select(m => m with { MessageId = Prefix + m.MessageId }).ToArray() };
    }
    public async ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken cancellationToken = default)
    {
        await recheck(account, cancellationToken);
        if (!request.MessageId.StartsWith(Prefix, StringComparison.Ordinal)) throw AgentCoreErrors.NotFound("Message reference is not in this account.");
        var message = await inner.ReadAsync(new(request.MessageId[Prefix.Length..]), cancellationToken);
        return message with { MessageId = Prefix + message.MessageId };
    }
    public ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException("Core owns drafts.");
    public ValueTask<EmailDraftSnapshot?> GetDraftAsync(string draftId, CancellationToken cancellationToken = default) => throw new NotSupportedException("Core owns drafts.");
    public async ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest request, CancellationToken cancellationToken = default)
    {
        await recheck(account, cancellationToken);
        if (request.ApprovedDraft.AccountId != account.AccountId) throw AgentCoreErrors.Forbidden("Draft account mismatch.");
        return new(EmailSendOutcome.Sent, "syn-sent-" + request.DraftId, null, null);
    }
}
