using AgentCore.Domain.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantSandboxWorkspaceRules
{
    public static async ValueTask<SandboxWorkspaceAccess> ResolveAsync(TenantSecurityState state, AgentRun run, HomeAccess policy, IamAuthorizer iam, CancellationToken ct)
    {
        var context = TenantWorkspaceRules.Context(run);
        var actor = run.Owner.ExecutorPrincipalId ?? throw new AccessDeniedException("executor_unavailable");
        async ValueTask<bool> Allowed(string key, AccessScope scope) =>
            (await iam.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed;
        var working = TenantWorkspaceRules.Working(context.TenantId, run.SessionId);
        if (!await Allowed("sessions.read", TenantSessionRules.Scope(context.TenantId, run.SessionId)).ConfigureAwait(false)
            || !await Allowed("workspaces.read", working).ConfigureAwait(false)) throw new AccessDeniedException("workspace_read_required");
        async ValueTask<HomeAccess> Home(Guid space)
        {
            if (policy == HomeAccess.Disabled || !await Allowed("spaces.read", TenantSpaceService.Scope(context.TenantId, space)).ConfigureAwait(false)
                || !await Allowed("workspaces.read", TenantWorkspaceRules.Home(context.TenantId, space)).ConfigureAwait(false)) return HomeAccess.Disabled;
            return policy == HomeAccess.ReadWrite && await Allowed("workspaces.write", TenantWorkspaceRules.Home(context.TenantId, space)).ConfigureAwait(false)
                && await Allowed("workspaces.delete", TenantWorkspaceRules.Home(context.TenantId, space)).ConfigureAwait(false)
                ? HomeAccess.ReadWrite : HomeAccess.ReadOnly;
        }
        return new(context, await Home(context.SpaceId).ConfigureAwait(false), await Home(context.PrivateSpaceId).ConfigureAwait(false),
            !(await Allowed("workspaces.write", working).ConfigureAwait(false) && await Allowed("workspaces.delete", working).ConfigureAwait(false)));
    }
    public static async ValueTask RequireCurrentAsync(TenantSecurityState state, AgentRun run, TenantAgent agent, AgentDefinition baseline,
        SandboxWorkspaceAccess expected, IamAuthorizer iam, CancellationToken ct)
    {
        var current = InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides);
        var policy = StateIsolationPolicy.Restrict(run.Admission.Configuration!.Definition.StateIsolationPolicy ?? StateIsolationPolicy.Default,
            current.StateIsolationPolicy ?? StateIsolationPolicy.Default).HomeAccess;
        Require(expected, await ResolveAsync(state, run, policy, iam, ct).ConfigureAwait(false));
    }
    public static void Require(SandboxWorkspaceAccess expected, SandboxWorkspaceAccess current)
    {
        if (expected != current) throw new AccessDeniedException("sandbox_workspace_authority_changed");
    }
}
