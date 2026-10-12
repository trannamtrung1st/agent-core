using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Email;

public sealed class EmailAccountService(ICredentialStore credentials, IAgentCredentialBindingStore bindings,
    IAgentInstanceStore instances, IEmailProviderFactory providers, IEmailStore store, IIdGenerator ids, TimeProvider time)
{
    public DateTimeOffset UtcNow => time.GetUtcNow();
    public bool IsEnabled => providers.IsEnabled;
    public bool IsSendEnabled => providers.IsSendEnabled;
    public const string SendSetupGuidance = "SMTP dispatch is disabled. Configure and verify outgoing mail in Connections → Credentials, then ask the operator to enable Email:SmtpEnabled. The draft can be kept for later approval.";
    public async ValueTask<IReadOnlyList<BoundEmailAccount>> AccountsAsync(ToolExecutionAdmission admission, CancellationToken ct)
    {
        var owner = await RequireOwner(admission, ct);
        var result = new List<BoundEmailAccount>();
        foreach (var binding in await bindings.ListBindingsAsync(owner, ct))
        {
            if (!Admitted(admission, binding)) continue;
            if (await credentials.GetAsync(binding.CredentialId, ct) is { Status: CredentialStatus.Active, Purpose: CredentialPurpose.EmailAccount, EmailAccount: { } profile } c)
                result.Add(new(binding.Reference, c.DisplayName, profile.EmailAddress, "ImapSmtp"));
        }
        return result;
    }
    public async ValueTask<EmailAccountContext> ResolveAsync(ToolExecutionAdmission admission, string? reference, CancellationToken ct)
    {
        if (!IsEnabled) throw AgentCoreErrors.Validation("provider_disabled: Email transport is disabled.");
        var owner = await RequireOwner(admission, ct);
        var eligible = new List<EmailAccountContext>();
        foreach (var b in await bindings.ListBindingsAsync(owner, ct))
        {
            if (!Admitted(admission, b) || reference is not null && b.Reference != reference) continue;
            if (await credentials.GetAsync(b.CredentialId, ct) is not { Status: CredentialStatus.Active, Kind: CredentialKind.Password,
                Purpose: CredentialPurpose.EmailAccount, EmailAccount: { } profile } c) continue;
            eligible.Add(new(owner, c.CredentialId, b.Reference, c.DisplayName, c.Revision, b.BindingId, b.Revision, profile));
        }
        return eligible.Count switch
        {
            1 => eligible[0],
            0 => throw AgentCoreErrors.Forbidden("no_eligible_account: No admitted active email account. Use email.request_account in an interactive user turn or configure Connections → Credentials."),
            _ => throw AgentCoreErrors.Validation("account_selection_required: Specify an accountRef from email.accounts.")
        };
    }
    public IEmailProvider Provider(EmailAccountContext context) => providers.Create(context);
    public async ValueTask<EmailDraftSnapshot> CreateDraftAsync(Guid sessionId, ToolExecutionAdmission admission,
        string? reference, EmailCreateDraftRequest request, CancellationToken ct)
    {
        var account = await ResolveAsync(admission, reference, ct);
        var draft = EmailDraftNormalizer.Normalize(new("core-draft-" + ids.NewId().ToString("N"), request.To, request.Cc,
            request.Bcc, request.Subject, request.Body, account.AccountId, account.AccountRef, account.Profile.EmailAddress,
            1, account.AccountRevision, account.BindingId, account.BindingRevision));
        await store.CreateDraftAsync(new(account.AgentInstanceId, sessionId, draft, time.GetUtcNow()), ct);
        return draft;
    }
    public async ValueTask<EmailDraftSnapshot> DraftAsync(Guid sessionId, ToolExecutionAdmission admission, string draftId,
        string? reference, CancellationToken ct)
    {
        var owner = await RequireOwner(admission, ct);
        var saved = await store.GetDraftAsync(draftId, ct);
        if (saved is null || saved.AgentInstanceId != owner || saved.SessionId != sessionId) throw AgentCoreErrors.NotFound("Draft was not found in this Session.");
        var draft = saved.Draft;
        if (reference is not null && reference != draft.AccountRef) throw AgentCoreErrors.Forbidden("Draft account does not match.");
        var account = await ResolveAsync(admission, draft.AccountRef, ct);
        if (account.AccountId != draft.AccountId || account.AccountRevision != draft.AccountRevision
            || account.BindingId != draft.BindingId || account.BindingRevision != draft.BindingRevision)
            throw AgentCoreErrors.Forbidden("stale_account: Account or binding changed. Create a new draft and approval.");
        return draft;
    }
    public async ValueTask<EmailSendResult> SendAsync(Guid sessionId, ToolExecutionAdmission admission, string draftId,
        string? reference, ToolApprovalGrant grant, CancellationToken ct)
    {
        if (!IsSendEnabled) throw AgentCoreErrors.Validation("smtp_disabled: " + SendSetupGuidance);
        var draft = await DraftAsync(sessionId, admission, draftId, reference, ct);
        var hash = EmailDraftNormalizer.ComputeSendActionHash(draft);
        if (grant.ToolName != ToolCatalog.EmailSend || grant.ActionHash != hash) throw AgentCoreErrors.Forbidden("stale_approval: Approval does not match account and exact draft content.");
        var account = await ResolveAsync(admission, draft.AccountRef, ct);
        ct.ThrowIfCancellationRequested();
        if (!IsSendEnabled) throw AgentCoreErrors.Validation("smtp_disabled: " + SendSetupGuidance);
        if (!await store.TryClaimSendAsync(new(draftId, account.AccountId, draft.Revision, grant.ApprovalId, hash, null, time.GetUtcNow()), ct))
        {
            var previous = await store.GetSendAsync(draftId, ct);
            return previous?.Result ?? new(EmailSendOutcome.Indeterminate, null, "dispatch_unconfirmed", "Delivery may have occurred. This draft will not be retried automatically.");
        }
        EmailSendResult result;
        try { result = await Provider(account).SendDraftAsync(new(draftId, draft), ct); }
        catch (Exception) { result = new(EmailSendOutcome.Indeterminate, null, "dispatch_unconfirmed", "Delivery could not be confirmed. Create a new draft only if an intentional resend is required."); }
        // Cancellation after external acceptance must not discard a known durable receipt.
        await store.CompleteSendAsync(draftId, result, CancellationToken.None);
        return result;
    }
    private async ValueTask<Guid> RequireOwner(ToolExecutionAdmission admission, CancellationToken ct)
    {
        if (admission.AgentInstanceId is not Guid id || id == Guid.Empty
            || await instances.FindAsync(id, ct) is not { Lifecycle: AgentInstanceLifecycle.Active })
            throw AgentCoreErrors.Forbidden("Active Agent Instance is required for email.");
        return id;
    }
    private static bool Admitted(ToolExecutionAdmission a, AgentCredentialBinding b) =>
        a.PinnedCredentialBindings is not null && a.PinnedCredentialBindings.Any(p => p.BindingId == b.BindingId
            && p.CredentialId == b.CredentialId && p.Reference == b.Reference && p.Revision == b.Revision);
}
