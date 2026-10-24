using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireAccountChangeHuman(Account account, AuthorizationAudit audit, HumanSessionReference? human)
    {
        if (human is null) return;
        if (human.AccountId != account.AccountId || human.PrincipalId != audit.ActorPrincipalId) throw new AccessDeniedException("initiator_unavailable");
        _ = RequireAccountHuman(human);
    }
    private void RequireSecurityWriteHuman(TenantSecurityState next, AuthorizationAudit audit, HumanSessionReference? human)
    {
        if (human is null) return;
        if (audit.Action == "members.accept_invitation")
        {
            if (!next.Users.Any(u => u.PrincipalId == audit.ActorPrincipalId && u.AccountId == human.AccountId && u.Status == IdentityStatus.Active))
                throw new AccessDeniedException("initiator_unavailable");
            _ = RequireAccountHuman(human);
        }
        else RequireAgentHuman(next.Tenant.TenantId, audit.ActorPrincipalId, human);
    }
}
