using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantBrowserOperationRules
{
    public static TenantBrowserOwner Owner(TenantSessionRoot owner) => new(owner.TenantId, owner.AgentInstanceId, owner.ExecutorPrincipalId, owner.SessionId);
    public static IReadOnlyList<(string Permission, AccessScope Scope)> Targets(Guid tenant, Guid instance, Guid session, bool readOnly) =>
        new[] { ("sessions.read", TenantSessionRules.Scope(tenant, session)),
            ("browser.read", AccessScope.ForResource(tenant, "agent", instance.ToString("D"))) }
        .Concat(readOnly ? [] : new[] { ("browser.use", AccessScope.ForResource(tenant, "agent", instance.ToString("D"))) }).ToArray();
    public static IReadOnlyList<(string Permission, AccessScope Scope)> Targets(Guid tenant, Guid instance, PreparedBrowserCommand value) =>
        Targets(tenant, instance, value.SessionId, value.ReadOnly).Concat(value.Credential is { } c
            ? new[] { ("credentials.read", TenantCredentialRules.Scope(tenant, c.CredentialId)), ("credentials.use", TenantCredentialRules.Scope(tenant, c.CredentialId)) } : [])
        .Concat(TenantApprovalRules.BrowserIntent(value.SessionId, value.ToolName, value.ArgumentsJson).Command is BrowserUploadCommand upload
            ? TenantBrowserUploadRules.Ids(upload).Select(id => ("artifacts.read", TenantArtifactRules.Scope(tenant, id))) : []).ToArray();
    public static async ValueTask RequirePermissionsAsync(TenantSecurityState state, Guid instance, PreparedBrowserCommand value, Guid principal,
        IamAuthorizer authorizer, CancellationToken ct, bool allowCredentialUseException = false)
    {
        foreach (var (permission, scope) in Targets(state.Tenant.TenantId, instance, value))
        {
            if (allowCredentialUseException && value.Credential is not null && permission == "credentials.use") continue;
            if (!(await authorizer.CheckTenantAsync(state, principal, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("permission_denied");
        }
    }
    public static async ValueTask<bool> NeedsReviewAsync(TenantSecurityState state, Guid instance, PreparedBrowserCommand value, Guid? requester,
        IamAuthorizer authorizer, CancellationToken ct)
    {
        foreach (var (permission, scope) in Targets(state.Tenant.TenantId, instance, value))
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, permission, scope, authorizer, ct).ConfigureAwait(false)) return true;
        return false;
    }
    public static async ValueTask<bool> IndependentAsync(TenantApprovalRequest request, TenantSecurityState state, IamAuthorizer authorizer, CancellationToken ct)
    {
        foreach (var (permission, scope) in Targets(request.TenantId, request.Operation.InstanceId, request.Operation.BrowserCommand!))
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission)).IndependentReview
                && (permission == request.Permission || await TenantSandboxReadOperationRules.NeedsReviewAsync(state, request.OriginalInitiatorPrincipalId, permission, scope, authorizer, ct).ConfigureAwait(false))) return true;
        return false;
    }
    public static async ValueTask RequireReviewerAsync(TenantSecurityState state, Guid instance, PreparedBrowserCommand value, Guid? requester,
        Guid human, IamAuthorizer authorizer, CancellationToken ct, ApprovalKind kind = ApprovalKind.Review)
    {
        if (!state.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approver_unqualified");
        await RequirePermissionsAsync(state, instance, value, human, authorizer, ct).ConfigureAwait(false);
        foreach (var (permission, scope) in Targets(state.Tenant.TenantId, instance, value))
        {
            if (permission != (value.Credential is not null ? "credentials.use" : value.ReadOnly ? "browser.read" : "browser.use")
                && !await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, permission, scope, authorizer, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission)).IndependentReview && requester == human)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await authorizer.CheckTenantAsync(state, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
        if (kind == ApprovalKind.ExceptionalGrant)
        {
            if (value.Credential is not { } credential || !PermissionCatalog.Get("credentials.use").Delegatable)
                throw new AccessDeniedException("nondelegatable");
            if (!(await authorizer.CheckTenantAsync(state, human, PermissionCatalog.Get("approvals.grant_exception"),
                TenantCredentialRules.Scope(state.Tenant.TenantId, credential.CredentialId), ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }
    // Pure saved-owner/source checks, repeated inside the approval transaction. No native I/O in a store transaction.
    public static void Require(PreparedBrowserCommand value, AgentRun run, TenantAgent agent, AgentDefinition baseline, TenantSession session)
    {
        RequireSource(value.SessionId, value.ToolName, run, agent, baseline, session);
        var admitted = run.Admission.Configuration!.Definition;
        var current = InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides);
        var target = TenantApprovalRules.BrowserTarget(value);
        if (Isolation(admitted, current) == BrowserIsolation.EphemeralSession && target.ProfileMode != BrowserProfileMode.EphemeralSession)
            throw new AccessDeniedException("browser_policy_changed");
    }
    public static void RequireSource(Guid sessionId, string tool, AgentRun run, TenantAgent agent, AgentDefinition baseline, TenantSession session)
    {
        var admitted = run.Admission.Configuration?.Definition ?? throw new AccessDeniedException("tenant_context_required");
        var current = InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides);
        if (!RolePermissions.AllowsTool(admitted, tool) || !RolePermissions.AllowsTool(current, tool))
            throw new AccessDeniedException("browser_policy_changed");
        if (session.Owner.TenantId != run.Owner.TenantId || session.Owner.SessionId != sessionId || run.SessionId != sessionId
            || session.Owner.AgentInstanceId != run.AgentInstanceId || session.Owner.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId
            || agent.PrincipalId != session.Owner.ExecutorPrincipalId || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null
            || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended)
            throw new AccessDeniedException("resource_unavailable");

    }
    public static BrowserIsolation Isolation(AgentDefinition admitted, AgentDefinition current) => StateIsolationPolicy.Restrict(
        admitted.StateIsolationPolicy ?? StateIsolationPolicy.Default, current.StateIsolationPolicy ?? StateIsolationPolicy.Default).BrowserIsolation;
    public static TenantApprovalSnapshot Snapshot(TenantAgent agent, PreparedBrowserCommand value)
    {
        var target = TenantApprovalRules.BrowserTarget(value);
        return new(agent.Instance.Persona.Name, Browser: new(value.ToolName, target.Origin, target.ProfileMode.ToString()));
    }
}
