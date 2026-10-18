using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantOperationPolicyView(string Permission, PermissionApprovalClass ApprovalClass,
    OperationReviewPolicy Minimum, OperationReviewPolicy Effective, long PolicyRevision, long SecurityRevision,
    bool HumanOnly, bool Delegatable, bool CanManage);
public sealed record TenantOperationPolicyPage(IReadOnlyList<TenantOperationPolicyView> Items, int Total, int Page, int PageSize, long SecurityRevision);

public sealed class TenantOperationPolicyService(IIamStore store, ITenantOperationPolicyStore policies, IamAuthorizer authorizer, IdentityService identity, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantOperationPolicyView> ReadAsync(Guid tenant, TenantSessionInitiator actor, string permission, CancellationToken ct = default)
    {
        var state = await Govern(tenant, actor, ct, manage: false).ConfigureAwait(false);
        var view = View(state, Metadata(permission), await CanManage(state, actor, ct).ConfigureAwait(false));
        await Fence(state, actor, ct).ConfigureAwait(false); return view;
    }
    public async ValueTask<TenantOperationPolicyPage> ListAsync(Guid tenant, TenantSessionInitiator actor,
        int page = 0, int pageSize = 20, string? search = null, CancellationToken ct = default)
    {
        if (page is < 0 or > 10000 || pageSize is < 1 or > 100 || search is { Length: > 160 }) throw new ArgumentException("Use bounded policy search and pagination.");
        var state = await Govern(tenant, actor, ct, manage: false).ConfigureAwait(false);
        var manage = await CanManage(state, actor, ct).ConfigureAwait(false);
        var query = (search ?? "").Trim();
        var matches = PermissionCatalog.All.Where(p => !p.SystemOnly && p.Key.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var items = matches.Skip(page * pageSize).Take(pageSize).Select(p => View(state, p, manage)).ToArray();
        await Fence(state, actor, ct).ConfigureAwait(false);
        return new(items, matches.Length, page, pageSize, state.Tenant.Revision);
    }
    public async ValueTask<TenantOperationPolicyView> SaveAsync(Guid tenant, TenantSessionInitiator actor, string permission,
        OperationReviewPolicy policy, long expectedSecurityRevision, long expectedPolicyRevision, CancellationToken ct = default)
    {
        var state = await Govern(tenant, actor, ct).ConfigureAwait(false); var metadata = Metadata(permission);
        var current = state.OperationPolicies.SingleOrDefault(p => p.PermissionKey == permission);
        if (state.Tenant.Revision != expectedSecurityRevision || (current?.Revision ?? 0) != expectedPolicyRevision) throw new SecurityConflictException("stale_revision");
        if (!policy.IsAtLeast(OperationReviewPolicy.Minimum(metadata.ApprovalClass))) throw new ArgumentException("Tenant policy cannot weaken Core approval requirements.");
        var value = new TenantOperationPolicy(tenant, permission, policy.Mode, policy.IndependentReview, checked(expectedPolicyRevision + 1));
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) },
            OperationPolicies = state.OperationPolicies.Where(p => p.PermissionKey != permission).Append(value).ToArray() };
        var audit = new AuthorizationAudit(ids.NewId(), tenant, actor.PrincipalId, "tenant.operation_policy.update", permission, "allowed", "operation_policy_updated", time.GetUtcNow());
        if (!await policies.TrySaveTenantOperationPolicyAsync(next, state.Tenant.Revision, actor, audit, ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        return View(next, metadata, true);
    }
    internal static OperationReviewPolicy Effective(TenantSecurityState state, PermissionDefinition permission)
    {
        var configured = state.OperationPolicies.SingleOrDefault(p => p.PermissionKey == permission.Key);
        var minimum = OperationReviewPolicy.Minimum(permission.ApprovalClass);
        if (configured is null) return minimum;
        var result = new OperationReviewPolicy(configured.Mode, configured.IndependentReview);
        if (!result.IsAtLeast(minimum)) throw new AccessDeniedException("invalid_operation_policy");
        return result;
    }
    private async ValueTask<TenantSecurityState> Govern(Guid tenant, TenantSessionInitiator actor, CancellationToken ct, bool manage = true)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(store, identity, tenant, actor, ct).ConfigureAwait(false);
        var result = await authorizer.CheckTenantAsync(state, actor.PrincipalId, PermissionCatalog.Get(manage ? "tenant.governance.manage" : "tenant.governance.read"), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        if (!result.Allowed) throw new AccessDeniedException(result.ReasonCode); return state;
    }
    private async ValueTask<bool> CanManage(TenantSecurityState state, TenantSessionInitiator actor, CancellationToken ct) =>
        (await authorizer.CheckTenantAsync(state, actor.PrincipalId, PermissionCatalog.Get("tenant.governance.manage"), AccessScope.ForTenant(state.Tenant.TenantId), ct).ConfigureAwait(false)).Allowed;
    private async ValueTask Fence(TenantSecurityState before, TenantSessionInitiator actor, CancellationToken ct)
    {
        var current = await Govern(before.Tenant.TenantId, actor, ct, manage: false).ConfigureAwait(false);
        if (current.Tenant.Revision != before.Tenant.Revision) throw new SecurityConflictException("stale_revision");
    }
    private static PermissionDefinition Metadata(string key) => PermissionCatalog.TryGet(key, out var metadata) && !metadata.SystemOnly
        ? metadata : throw new ArgumentException("Unknown tenant operation permission.");
    private static TenantOperationPolicyView View(TenantSecurityState state, PermissionDefinition metadata, bool manage) => new(metadata.Key, metadata.ApprovalClass,
        OperationReviewPolicy.Minimum(metadata.ApprovalClass), Effective(state, metadata), state.OperationPolicies.SingleOrDefault(p => p.PermissionKey == metadata.Key)?.Revision ?? 0, state.Tenant.Revision,
        metadata.HumanOnly, metadata.Delegatable, manage);
}
