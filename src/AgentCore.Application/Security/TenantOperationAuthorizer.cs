using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantRunReference(Guid TenantId, Guid RunId);
public sealed record OperationAuthorizationDecision(OperationDecisionKind Decision, string ReasonCode, string Permission,
    AccessScope Resource, ApprovalKind? ApprovalKind = null);

// Decisions are current inspection results, not capabilities or operation grants. Effects must revalidate at commit.
public sealed class TenantOperationAuthorizer(IIamStore store, ITenantRunStore runs, ITenantAgentStore agents, IamAuthorizer authorizer)
{
    public async ValueTask<OperationAuthorizationDecision> CheckAsync(TenantRunReference reference, string permission, string resourceType, string resourceId, CancellationToken ct = default)
    {
        var (run, state) = await ResolveAsync(reference, ct).ConfigureAwait(false);
        AccessScope target;
        try { target = IamResourceScopes.Create(reference.TenantId, resourceType, resourceId); }
        catch (AccessDeniedException) { return new(OperationDecisionKind.Denied, "resource_unavailable", permission, AccessScope.ForTenant(reference.TenantId)); }
        return await Check(state, run.Admission.ExecutionContext!, permission, target, ct).ConfigureAwait(false);
    }
    public async ValueTask<IReadOnlyList<OperationAuthorizationDecision>> EffectiveAsync(TenantRunReference reference, string resourceType, string resourceId, CancellationToken ct = default)
    {
        var (run, state) = await ResolveAsync(reference, ct).ConfigureAwait(false);
        var target = IamResourceScopes.Create(reference.TenantId, resourceType, resourceId);
        var result = new List<OperationAuthorizationDecision>();
        foreach (var permission in PermissionCatalog.All.Where(p => !p.SystemOnly && !p.HumanOnly && p.AppliesToResource(target.ResourceType)))
        {
            if (!(await authorizer.CheckTenantAsync(state, run.Admission.ExecutionContext!.AgentPrincipalId, permission, target, ct).ConfigureAwait(false)).Allowed) continue;
            var decision = await Check(state, run.Admission.ExecutionContext!, permission.Key, target, ct).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.Denied && decision.ApprovalKind != ApprovalKind.ExceptionalGrant) result.Add(decision);
        }
        return result;
    }
    internal async ValueTask<(AgentRun Run, TenantSecurityState State)> ResolveAsync(TenantRunReference reference, CancellationToken ct)
    {
        var run = await runs.FindTenantRunAsync(reference.TenantId, reference.RunId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("run_unavailable");
        var context = run.Admission.ExecutionContext;
        if (context is null || context.TenantId != reference.TenantId || run.IsTerminal) throw new AccessDeniedException("run_unavailable");
        var state = await store.ReadTenantAsync(reference.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        if (state.Tenant.Status != IdentityStatus.Active) throw new AccessDeniedException("tenant_unavailable");
        var agent = await agents.FindTenantAgentAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("executor_unavailable");
        if (agent.PrincipalId != context.AgentPrincipalId || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("executor_unavailable");
        TenantSessionRules.RequireExecutor(state, agent);
        if (context.OriginalInitiatorPrincipalId is { } requester)
            foreach (var pair in new[] { ("sessions.read", TenantSessionRules.Scope(reference.TenantId, run.SessionId)),
                ("agents.chat", AccessScope.ForResource(reference.TenantId, "agent", run.AgentInstanceId.ToString("D"))) })
            {
                var current = await authorizer.CheckTenantAsync(state, requester, PermissionCatalog.Get(pair.Item1), pair.Item2, ct).ConfigureAwait(false);
                if (!current.Allowed) throw new AccessDeniedException("requester_unavailable");
            }
        return (run, state);
    }
    private async ValueTask<OperationAuthorizationDecision> Check(TenantSecurityState state, TrustedExecutionContext context, string key, AccessScope target, CancellationToken ct)
    {
        OperationAuthorizationDecision Deny(string code) => new(OperationDecisionKind.Denied, code, key, target);
        if (!PermissionCatalog.TryGet(key, out var permission)) return Deny("unknown_permission");
        if (permission.HumanOnly || permission.SystemOnly) return Deny("human_only");
        var executor = await authorizer.CheckTenantAsync(state, context.AgentPrincipalId, permission, target, ct).ConfigureAwait(false);
        var policy = TenantOperationPolicyService.Effective(state, permission);
        ApprovalKind? kind = null;
        if (!executor.Allowed)
        {
            if (executor.ReasonCode != "permission_denied") return Deny(executor.ReasonCode);
            if (!permission.Delegatable) return Deny("nondelegatable");
            kind = ApprovalKind.ExceptionalGrant;
        }
        else if (policy.Mode == OperationApprovalMode.Always) kind = ApprovalKind.Review;
        else if (policy.Mode == OperationApprovalMode.WhenRequesterLacksPermission && context.OriginalInitiatorPrincipalId is { } requester
            && !(await authorizer.CheckTenantAsync(state, requester, permission, target, ct).ConfigureAwait(false)).Allowed) kind = ApprovalKind.Review;
        if (kind is null) return new(OperationDecisionKind.Allowed, "allowed", key, target);
        foreach (var user in state.Users.Where(u => u.Status == IdentityStatus.Active && (!policy.IndependentReview || u.PrincipalId != context.OriginalInitiatorPrincipalId)))
        {
            if (!(await authorizer.CheckTenantAsync(state, user.PrincipalId, permission, target, ct).ConfigureAwait(false)).Allowed) continue;
            if (!(await authorizer.CheckTenantAsync(state, user.PrincipalId, PermissionCatalog.Get("approvals.approve"), target, ct).ConfigureAwait(false)).Allowed) continue;
            if (kind == ApprovalKind.ExceptionalGrant && !(await authorizer.CheckTenantAsync(state, user.PrincipalId, PermissionCatalog.Get("approvals.grant_exception"), target, ct).ConfigureAwait(false)).Allowed) continue;
            return new(OperationDecisionKind.RequiresApproval, kind == ApprovalKind.Review ? "review_required" : "agent_missing_delegatable_permission", key, target, kind);
        }
        return Deny("no_eligible_approver");
    }
}
