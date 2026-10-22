using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantRunReference(Guid TenantId, Guid RunId);
public sealed record OperationAuthorizationDecision(OperationDecisionKind Decision, string ReasonCode, string Permission,
    AccessScope Resource, ApprovalKind? ApprovalKind = null);

// Decisions are current inspection results, not capabilities or operation grants. Effects must revalidate at commit.
public sealed class TenantOperationAuthorizer(IIamStore store, ITenantRunStore runs, ITenantAgentStore agents, IamAuthorizer authorizer, TenantOperationAudit audit, TenantEventReadAuthority? eventReads = null)
{
    internal ValueTask<AgentRun?> FindRunAsync(TenantRunReference reference, CancellationToken ct) =>
        runs.FindTenantRunAsync(reference.TenantId, reference.RunId, ct);
    public async ValueTask<OperationAuthorizationDecision> CheckAsync(TenantRunReference reference, string permission, string resourceType, string resourceId, CancellationToken ct = default)
    {
        var (run, state) = await ResolveForDecisionAsync(reference, permission, ExecutionAuthorizationPurpose.Inspection, ct).ConfigureAwait(false);
        AccessScope target;
        try { target = IamResourceScopes.Create(reference.TenantId, resourceType, resourceId); }
        catch (AccessDeniedException)
        {
            var unavailable = new OperationAuthorizationDecision(OperationDecisionKind.Denied, "resource_unavailable", permission, AccessScope.ForTenant(reference.TenantId));
            await audit.RecordAsync(state, run, unavailable, ExecutionAuthorizationPurpose.Inspection, ct).ConfigureAwait(false); return unavailable;
        }
        return await Check(state, run, permission, target, ct, purpose: ExecutionAuthorizationPurpose.Inspection).ConfigureAwait(false);
    }
    public async ValueTask<IReadOnlyList<OperationAuthorizationDecision>> EffectiveAsync(TenantRunReference reference, string resourceType, string resourceId, CancellationToken ct = default)
    {
        var (run, state) = await ResolveForDecisionAsync(reference, "", ExecutionAuthorizationPurpose.Inspection, ct).ConfigureAwait(false);
        AccessScope target;
        try { target = IamResourceScopes.Create(reference.TenantId, resourceType, resourceId); }
        catch (AccessDeniedException)
        {
            await audit.RecordAsync(state, run, new(OperationDecisionKind.Denied, "resource_unavailable", "", AccessScope.ForTenant(reference.TenantId)), ExecutionAuthorizationPurpose.Inspection, ct).ConfigureAwait(false);
            throw;
        }
        if (target.Kind == AccessScopeKind.Resource && !IamResourceScopes.Exists(state, target)
            && (!IamResourceScopes.ExternalShape(target) || !await store.OwnsResourceAsync(target, ct).ConfigureAwait(false)))
        {
            await audit.RecordAsync(state, run, new(OperationDecisionKind.Denied, "resource_unavailable", "", target), ExecutionAuthorizationPurpose.Inspection, ct).ConfigureAwait(false);
            return [];
        }
        var result = new List<OperationAuthorizationDecision>();
        foreach (var permission in PermissionCatalog.All.Where(p => !p.SystemOnly && !p.HumanOnly && p.AppliesToResource(target.ResourceType)))
        {
            if (!(await authorizer.CheckTenantAsync(state, run.Admission.ExecutionContext!.AgentPrincipalId, permission, target, ct).ConfigureAwait(false)).Allowed) continue;
            var decision = await Check(state, run, permission.Key, target, ct, purpose: ExecutionAuthorizationPurpose.Inspection).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.Denied && decision.ApprovalKind != ApprovalKind.ExceptionalGrant) result.Add(decision);
        }
        await audit.RecordAsync(state, run, new(OperationDecisionKind.Allowed, "effective_permissions_inspected", "", target), ExecutionAuthorizationPurpose.Inspection, ct).ConfigureAwait(false);
        return result;
    }
    internal async ValueTask<(AgentRun Run, TenantSecurityState State)> ResolveForDecisionAsync(TenantRunReference reference, string permission, ExecutionAuthorizationPurpose purpose, CancellationToken ct)
    {
        try { return await ResolveAsync(reference, ct).ConfigureAwait(false); }
        catch (AccessDeniedException ex)
        { await RecordFailureAsync(reference, permission, ex.ReasonCode, purpose, ct).ConfigureAwait(false); throw; }
    }
    internal async ValueTask RecordFailureAsync(TenantRunReference reference, string permission, string reason,
        ExecutionAuthorizationPurpose purpose, CancellationToken ct, Guid? requiredSession = null)
    {
        var run = await runs.FindTenantRunAsync(reference.TenantId, reference.RunId, ct).ConfigureAwait(false);
        if (run?.Admission.ExecutionContext is not { } context || context.TenantId != reference.TenantId
            || requiredSession is { } session && run.SessionId != session) return;
        var state = await store.ReadTenantAsync(reference.TenantId, ct).ConfigureAwait(false);
        if (state is not null) await audit.RecordAsync(state, run,
            new(OperationDecisionKind.Denied, reason, permission, AccessScope.ForTenant(reference.TenantId)), purpose, ct).ConfigureAwait(false);
    }
    internal ValueTask<AgentRun> RootAsync(AgentRun run, CancellationToken ct) => TenantExecutionLineage.RootAsync(run, runs, ct);
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
        await TenantExecutionLineage.RequireRequesterAsync(run, state, authorizer, runs, ct).ConfigureAwait(false);
        if (eventReads is not null) await eventReads.RequireAsync(run, ct).ConfigureAwait(false);
        else if (TenantEventReadRules.IsEventRoot(await TenantExecutionLineage.RootAsync(run, runs, ct).ConfigureAwait(false)) || TenantAutomationConversationRules.IsConversationRoot(await TenantExecutionLineage.RootAsync(run, runs, ct).ConfigureAwait(false)))
            throw new AccessDeniedException("event_source_unavailable");
        return (run, state);
    }
    internal async ValueTask<OperationAuthorizationDecision> Check(TenantSecurityState state, AgentRun run, string key, AccessScope target, CancellationToken ct,
        bool managementReviewRequired = false, ExecutionAuthorizationPurpose purpose = ExecutionAuthorizationPurpose.EffectEligibility)
    {
        var decision = await Evaluate(state, run.Admission.ExecutionContext!, key, target, ct, managementReviewRequired || TenantConfigurationContinuationRules.Target(run) is not null && PermissionCatalog.TryGet(key, out var continuationPermission) && continuationPermission.ApprovalClass != PermissionApprovalClass.ReadOnly).ConfigureAwait(false);
        await audit.RecordAsync(state, run, decision, purpose, ct).ConfigureAwait(false); return decision;
    }
    internal ValueTask RecordAsync(TenantSecurityState state, AgentRun run, OperationAuthorizationDecision decision,
        ExecutionAuthorizationPurpose purpose, CancellationToken ct) => audit.RecordAsync(state, run, decision, purpose, ct);

    internal async ValueTask<OperationAuthorizationDecision> Evaluate(TenantSecurityState state, TrustedExecutionContext context, string key, AccessScope target, CancellationToken ct, bool managementReviewRequired = false)
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
        else if (policy.Mode == OperationApprovalMode.Always || managementReviewRequired || context.Origin == AgentCore.Domain.Conversation.ExecutionOrigin.ConfigurationContinuation && permission.ApprovalClass != PermissionApprovalClass.ReadOnly) kind = ApprovalKind.Review;
        else if (policy.Mode == OperationApprovalMode.WhenRequesterLacksPermission && context.OriginalInitiatorPrincipalId is { } requester
            && !(await authorizer.CheckTenantAsync(state, requester, permission, target, ct).ConfigureAwait(false)).Allowed) kind = ApprovalKind.Review;
        if (kind is null) return new(OperationDecisionKind.Allowed, "allowed", key, target);
        foreach (var user in state.Users.Where(u => u.Status == IdentityStatus.Active && (!policy.IndependentReview || u.PrincipalId != context.OriginalInitiatorPrincipalId)))
        {
            if (!await QualifiesAsync(state, context.OriginalInitiatorPrincipalId, user.PrincipalId, permission, target, kind.Value, ct).ConfigureAwait(false)) continue;
            return new(OperationDecisionKind.RequiresApproval, kind == ApprovalKind.Review ? "review_required" : "agent_missing_delegatable_permission", key, target, kind);
        }
        return Deny("no_eligible_approver");
    }

    internal async ValueTask<bool> QualifiesAsync(TenantSecurityState state, Guid? requester, Guid human,
        PermissionDefinition permission, AccessScope target, ApprovalKind kind, CancellationToken ct)
    {
        if (!state.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)
            || EffectiveIndependent() && human == requester) return false;
        foreach (var key in kind == ApprovalKind.ExceptionalGrant
            ? new[] { permission.Key, "approvals.approve", "approvals.grant_exception" } : new[] { permission.Key, "approvals.approve" })
            if (!(await authorizer.CheckTenantAsync(state, human, PermissionCatalog.Get(key), target, ct).ConfigureAwait(false)).Allowed) return false;
        return true;
        bool EffectiveIndependent() => TenantOperationPolicyService.Effective(state, permission).IndependentReview;
    }
}
