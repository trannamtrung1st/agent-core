using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async Task RequireAccountChangeHumanAsync(AgentCoreDbContext db, Account account, AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct)
    {
        if (human is null) return;
        if (human.AccountId != account.AccountId || human.PrincipalId != audit.ActorPrincipalId) throw new AccessDeniedException("initiator_unavailable");
        _ = await RequireAccountHumanAsync(db, human, ct).ConfigureAwait(false);
    }
    private async Task RequireSecurityWriteHumanAsync(AgentCoreDbContext db, TenantSecurityState next, AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct)
    {
        if (human is null) return;
        if (audit.Action == "members.accept_invitation")
        {
            if (!next.Users.Any(u => u.PrincipalId == audit.ActorPrincipalId && u.AccountId == human.AccountId && u.Status == IdentityStatus.Active))
                throw new AccessDeniedException("initiator_unavailable");
            _ = await RequireAccountHumanAsync(db, human, ct).ConfigureAwait(false);
        }
        else await RequireAgentHumanAsync(db, next.Tenant.TenantId, audit.ActorPrincipalId, human, ct).ConfigureAwait(false);
    }
}
