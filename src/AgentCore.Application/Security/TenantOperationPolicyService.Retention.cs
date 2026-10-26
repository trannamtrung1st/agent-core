using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantChatRetentionView(int? SavedChatRetentionDays, long SecurityRevision, bool CanManage);

public sealed partial class TenantOperationPolicyService
{
    public async ValueTask<TenantChatRetentionView> ReadChatRetentionAsync(Guid tenant, TenantSessionInitiator actor, CancellationToken ct = default)
    {
        var state = await Govern(tenant, actor, ct, manage: false).ConfigureAwait(false);
        var manage = await CanManage(state, actor, ct).ConfigureAwait(false);
        await Fence(state, actor, ct).ConfigureAwait(false);
        return new(state.Tenant.SavedChatRetentionDays, state.Tenant.Revision, manage);
    }

    public async ValueTask<TenantChatRetentionView> SaveChatRetentionAsync(Guid tenant, TenantSessionInitiator actor,
        int? days, long expectedSecurityRevision, CancellationToken ct = default)
    {
        var state = await Govern(tenant, actor, ct).ConfigureAwait(false);
        if (days is < 1 or > 3650) throw new ArgumentException("Saved Chat retention must be Forever or 1–3650 days after completion.");
        if (state.Tenant.Revision != expectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        var next = state with { Tenant = state.Tenant with { SavedChatRetentionDays = days, Revision = checked(state.Tenant.Revision + 1) } };
        var audit = new AuthorizationAudit(ids.NewId(), tenant, actor.PrincipalId, "tenant.chat_retention.update", tenant.ToString("D"),
            "allowed", "chat_retention_updated", time.GetUtcNow());
        if (!await policies.TrySaveTenantOperationPolicyAsync(next, state.Tenant.Revision, actor, audit, ct).ConfigureAwait(false))
            throw new SecurityConflictException("stale_revision");
        return new(days, next.Tenant.Revision, true);
    }
}
