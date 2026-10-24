using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Native fixture helper only; canonical host administration uses SystemAdministrationService.
internal sealed class AccountAdministrationService(IIamStore store, IamAuthorizer authorizer, IIdentityTokens tokens,
    IIdGenerator ids, TimeProvider time)
{
    public async ValueTask SetStatusAsync(Guid systemActorId, Guid accountId, IdentityStatus status, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentException("Unknown Account status.", nameof(status));
        await authorizer.RequireAsync(systemActorId, "system.accounts.manage", AccessScope.System, ct).ConfigureAwait(false);
        var account = await store.ReadAccountAsync(accountId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("account_unavailable");
        var next = account with { Status = status, SecurityStamp = tokens.Create() };
        var audit = new AuthorizationAudit(ids.NewId(), null, systemActorId, "system.accounts.status", accountId.ToString("D"), "allowed", "authorized", time.GetUtcNow());
        if (!await store.TryUpdateAccountAsync(next, account.SecurityStamp, audit, ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_account");
    }
}
