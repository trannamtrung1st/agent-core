using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<SandboxLease?> TryRequestTenantSandboxResetAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSandboxReset reset, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        var tenant = next.Tenant.TenantId; TenantSandboxResetRules.Audit(tenant, reset, human, audit); SandboxLease? requested = null;
        bool written;
        try
        {
            written = await WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
            {
                await RequireAgentHumanAsync(db, tenant, human.PrincipalId, human, ct).ConfigureAwait(false);
                var session = await ReadSandboxSession(db, tenant, reset.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
                var row = await db.Set<SandboxLeaseRow>().SingleOrDefaultAsync(l => l.TenantId == tenant && l.SessionId == reset.SessionId.ToString("D"), ct).ConfigureAwait(false)
                    ?? throw new AccessDeniedException("resource_unavailable");
                var lease = SandboxRuntimePersistence.Value(row);
                var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
                await TenantSandboxResetRules.RequirePermissionsAsync(state, session.Owner, lease.Profile.Id, human, new IamAuthorizer(this), ct).ConfigureAwait(false);
                TenantSandboxResetRules.Require(reset, session, lease); requested = TenantSandboxResetRules.Apply(lease);
                db.Entry(row).CurrentValues.SetValues(SandboxRuntimePersistence.Row(requested)); return true;
            }, ct).ConfigureAwait(false);
        }
        catch (AccessDeniedException error) when (error.ReasonCode is "principal_unavailable" or "account_unavailable")
        { throw new AccessDeniedException("initiator_unavailable"); }
        return written ? requested : null;
    }
}
