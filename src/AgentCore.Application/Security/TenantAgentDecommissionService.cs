using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAgentDecommissionView(Guid InstanceId, string AgentName, string Lifecycle, long InstanceRevision,
    long SecurityRevision, bool Decommissioned, bool CanDecommission, string? Blocker, Guid? OperationId);
public sealed record TenantAgentDecommissionResult(Guid OperationId, string Status, string CleanupStatus);

public sealed class TenantAgentDecommissionService(IIamStore security, ITenantAgentDecommissionStore store,
    IdentityService identity, IamAuthorizer authorizer, TimeProvider time, Admin.AdminLifecycleCoordinator lifecycle,
    TenantAgentDecommissionCleanup cleanup)
{
    private async ValueTask<(TenantSecurityState State, TenantAgentDecommissionSnapshot Snapshot)> Require(Guid tenant, Guid instance,
        HumanSessionReference human, bool delete, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
        var snapshot = await store.ReadAgentDecommissionAsync(tenant, instance, ct) ?? throw new AccessDeniedException("resource_unavailable");
        if (!await TenantAgentDecommissionRules.Allowed(state, human.PrincipalId, instance, authorizer, delete, ct)) throw new AccessDeniedException("permission_denied");
        return (state, snapshot);
    }
    public async ValueTask<TenantAgentDecommissionView> ReadAsync(Guid tenant, Guid instance, HumanSessionReference human, CancellationToken ct = default)
    {
        var before = await Require(tenant, instance, human, false, ct); var after = await Require(tenant, instance, human, false, ct);
        if (before.State.Tenant.Revision != after.State.Tenant.Revision || before.Snapshot.Agent.Instance.Revision != after.Snapshot.Agent.Instance.Revision) throw new SecurityConflictException("stale_revision");
        var snapshot = after.Snapshot; var agent = snapshot.Agent; var retired = snapshot.Status == IdentityStatus.Removed;
        var blocker = retired ? "agent_decommissioned" : agent.Instance.Lifecycle != AgentInstanceLifecycle.Archived ? "archive_required"
            : snapshot.References.HasReferences ? "agent_in_use" : agent.Instance.BrowserProfileReset?.Status == "pending" ? "browser_profile_busy" : null;
        var canDecommission = blocker is null && await TenantAgentDecommissionRules.Allowed(after.State, human.PrincipalId, instance, authorizer, true, ct);
        var final = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
        if (final.Tenant.Revision != after.State.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return new(instance, agent.Instance.Persona.Name, agent.Instance.Lifecycle.ToString(), agent.Instance.Revision, after.State.Tenant.Revision,
            retired, canDecommission, blocker, snapshot.Receipt?.AuditId);
    }
    public ValueTask<TenantAgentDecommissionResult> DecommissionAsync(Guid tenant, Guid instance, HumanSessionReference human,
        long expectedSecurityRevision, long expectedInstanceRevision, Guid operation, CancellationToken ct = default) =>
        lifecycle.WithInstanceAsync(instance, token => Execute(tenant, instance, human, expectedSecurityRevision, expectedInstanceRevision, operation, token), ct);
    private async ValueTask<TenantAgentDecommissionResult> Execute(Guid tenant, Guid instance, HumanSessionReference human,
        long expectedSecurityRevision, long expectedInstanceRevision, Guid operation, CancellationToken ct)
    {
        if (operation == Guid.Empty || expectedSecurityRevision < 1 || expectedInstanceRevision < 1) throw new ArgumentException("Use a current decommission witness and fresh operation ID.");
        var current = await Require(tenant, instance, human, true, ct);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        var audit = new AuthorizationAudit(operation, tenant, human.PrincipalId, TenantAgentDecommissionRules.Action, instance.ToString("D"), "allowed", "agent_decommissioned", at);
        if (!await store.TryDecommissionAgentAsync(tenant, instance, human, expectedSecurityRevision, expectedInstanceRevision, audit, ct)) throw new SecurityConflictException("stale_revision");
        // Committed retirement is the technical cleanup mandate, independent of later login/caller cancellation.
        var owner = new TenantBrowserAgentOwner(tenant, instance, current.Snapshot.Agent.PrincipalId);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        var complete = await cleanup.ReconcileAsync(owner, budget.Token);
        return new(operation, "decommissioned", complete ? "completed" : "pending");
    }
}
