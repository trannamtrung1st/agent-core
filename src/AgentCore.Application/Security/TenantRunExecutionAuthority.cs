using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantRunExecutionAuthority(TenantOperationAuthorizer operations, IIamStore store, TimeProvider time,
    TenantRunConfigurationResolver configurations, TenantBrowserEffectService? browserEffects = null, TenantBrowserReadService? browserReads = null,
    TenantCredentialRuntimeReadService? credentialReads = null, TenantArtifactRuntimeReadService? artifactReads = null, TenantWorkspaceRuntimeReadService? workspaceReads = null) : IAgentRunAuthority, ITenantRunContext
{
    public async ValueTask<AgentDefinition?> CurrentDefinitionAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        try
        {
            // Discard a raced policy snapshot and make one fresh read. This never
            // retries a model call or effect, and every attempt rechecks live authority.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = await ResolveCurrentAsync(run, cancellationToken).ConfigureAwait(false);
                var current = await configurations.CurrentPolicyAsync(before.State.Tenant.TenantId, run.AgentInstanceId, cancellationToken).ConfigureAwait(false);
                var after = await ResolveCurrentAsync(run, cancellationToken).ConfigureAwait(false);
                if (current.Agent.PrincipalId != run.Owner.ExecutorPrincipalId
                    || current.Agent.Instance.Lifecycle != AgentInstanceLifecycle.Active || current.Agent.Instance.DefinitionId != run.DefinitionId)
                    return null;
                if (before.State.Tenant.Revision == after.State.Tenant.Revision)
                {
                    if (browserEffects is not null) await browserEffects.RequireStoredObservationAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    if (browserReads is not null) await browserReads.RequireStoredObservationsAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    if (credentialReads is not null) await credentialReads.RequireStoredAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    if (artifactReads is not null) await artifactReads.RequireStoredAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    if (workspaceReads is not null) await workspaceReads.RequireStoredAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    return current.Definition;
                }
            }
            return null;
        }
        catch (AccessDeniedException) { return null; }
        catch (SecurityConflictException) { return null; }
    }

    private async ValueTask<(AgentRun Run, TenantSecurityState State)> ResolveCurrentAsync(AgentRun run, CancellationToken ct)
    {
        if (run.Owner.TenantId is not { } tenant || run.Admission.ExecutionContext is null)
            throw new AccessDeniedException("tenant_context_required");
        var (current, state) = await operations.ResolveAsync(new(tenant, run.AgentRunId), ct).ConfigureAwait(false);
        if (current.Owner != run.Owner || current.SessionId != run.SessionId || current.Admission.ExecutionContext != run.Admission.ExecutionContext
            || current.Status != AgentRunStatus.Running || current.CancellationRequested || current.Claim is not { } claim
            || claim.Generation != run.Claim?.Generation || claim.LeaseExpiresAtUtc <= time.GetUtcNow())
            throw new AccessDeniedException("tenant_runtime_unavailable");
        return (current, state);
    }

    public async ValueTask<TenantRequesterContext> CurrentRequesterAsync(AgentRun run, CancellationToken ct = default)
    {
        var (current, state) = await ResolveCurrentAsync(run, ct).ConfigureAwait(false);
        var execution = current.Admission.ExecutionContext!;
        Account? account = null; var hints = new List<TenantRequesterRole>();
        if (execution.OriginalInitiatorPrincipalId is { } requester)
        {
            var user = state.Users.SingleOrDefault(u => u.PrincipalId == requester && u.Status == IdentityStatus.Active)
                ?? throw new AccessDeniedException("requester_unavailable");
            account = await store.ReadAccountAsync(user.AccountId, ct).ConfigureAwait(false);
            if (account?.Status != IdentityStatus.Active) throw new AccessDeniedException("requester_unavailable");
            var agent = AccessScope.ForResource(execution.TenantId, "agent", current.AgentInstanceId.ToString("D"));
            var session = TenantSessionRules.Scope(execution.TenantId, current.SessionId);
            foreach (var assignment in state.Assignments.Where(a => a.PrincipalId == requester && a.TenantId == execution.TenantId
                && IamAuthorizer.ValidScope(a.Scope) && (a.Scope.Contains(agent) || a.Scope.Contains(session))))
            {
                var role = state.Roles.SingleOrDefault(r => r.RoleId == assignment.RoleId && r.TenantId == execution.TenantId);
                if (role is null || !role.Permissions.Any(key => PermissionCatalog.TryGet(key, out var p) && !p.SystemOnly
                    && (assignment.Scope.Contains(agent) && p.AppliesToResource("agent") || assignment.Scope.Contains(session) && p.AppliesToResource("session")))) continue;
                hints.Add(new(role.Name, assignment.Scope.Kind == AccessScopeKind.Tenant ? "organization" : assignment.Scope.ResourceType == "agent" ? "agent" : "conversation"));
            }
        }
        var ordered = hints.Distinct().OrderBy(h => h.Scope, StringComparer.Ordinal).ThenBy(h => h.Name, StringComparer.Ordinal).ToArray();
        // Account status/name changes do not necessarily change the tenant security revision.
        var (_, after) = await ResolveCurrentAsync(run, ct).ConfigureAwait(false);
        if (after.Tenant.Revision != state.Tenant.Revision || account is not null && await store.ReadAccountAsync(account.AccountId, ct).ConfigureAwait(false) != account)
            throw new SecurityConflictException("requester_context_changed");
        return new(execution, state.Tenant.Name, account?.DisplayName, ordered.Take(TenantRequesterContext.MaxRoleHints),
            ordered.Length > TenantRequesterContext.MaxRoleHints, state.Tenant.Revision, time.GetUtcNow());
    }
}
