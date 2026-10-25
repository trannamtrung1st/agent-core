using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using AgentCore.Domain.Triggers;
using SpaceEnvironment = AgentCore.Domain.Spaces.Environment;

namespace AgentCore.Application.Security;

public static class TenantAutomationContextRules
{
    public static string? Failure(string code) => code switch {
        "permission_denied" => "automation_context_permission_required",
        "automation_context_permission_required" or "space_unavailable" or "private_space_unavailable" or "space_membership_unavailable"
            or "environment_unavailable" or "environments_disabled" or "environment_selection_expired" or "environment_activation_required"
            or "automation_context_mismatch" => code,
        _ => null
    };
    public static bool IsSuspension(Automation value) => value.Status == AutomationStatus.SuspendedPolicy
        && value.SuspensionReason is { } reason && reason.StartsWith("context:", StringComparison.Ordinal) && Failure(reason[8..]) == reason[8..];
    public static bool CheckAuthoring(Automation? current, Automation proposed) => (proposed.PinnedSpaceId is not null
        || TenantAutomationConversationRules.Existing(proposed)) && (current is null || proposed.Status == AutomationStatus.Active
            || current.PinnedSpaceId != proposed.PinnedSpaceId || current.PinnedEnvironmentId != proposed.PinnedEnvironmentId || current.ExecutionTarget != proposed.ExecutionTarget);

    public static void Require(Automation value, TenantAgent agent, Space own, Space? active, SpaceMembership? membership,
        SpaceEnvironment? environment, TenantSession? conversation)
    {
        var tenant = value.Owner.TenantId!.Value; var wanted = value.PinnedSpaceId ?? own.SpaceId;
        if (agent.TenantId != tenant || own.TenantId != tenant || own.Kind != SpaceKind.Private || own.OwnerInstanceId != agent.Instance.InstanceId || own.DeletedAt is not null
            || active is null || active.TenantId != tenant || active.SpaceId != wanted || active.DeletedAt is not null
            || active.Kind == SpaceKind.Private && active.OwnerInstanceId != agent.Instance.InstanceId
            || membership is null || membership.TenantId != tenant || membership.SpaceId != wanted || membership.InstanceId != agent.Instance.InstanceId)
            throw new AccessDeniedException("space_membership_unavailable");
        if (value.PinnedEnvironmentId is { } id && (!active.EnvironmentsEnabled || environment is null || environment.EnvironmentId != id
            || environment.TenantId != tenant || environment.SpaceId != wanted || environment.DeletedAt is not null))
            throw new AccessDeniedException("environment_unavailable");
        if (TenantAutomationConversationRules.Existing(value))
        {
            var selected = TenantAutomationConversationRules.Require(value, conversation).Snapshot.ContextSelection;
            if (selected is null || selected.TenantId != tenant || selected.SpaceId != wanted || selected.EnvironmentId != value.PinnedEnvironmentId
                || selected.EnvironmentSelectionEpoch != (value.PinnedEnvironmentId is null ? 0 : active.EnvironmentSelectionEpoch))
                throw new AccessDeniedException("automation_context_mismatch");
        }
    }

    public static async ValueTask PermissionsAsync(TenantSecurityState state, Guid executor, Guid space, bool selectedEnvironment,
        Guid? human, IamAuthorizer iam, CancellationToken ct)
    {
        await TenantSpaceContextResolver.RequireCurrentPermissionsAsync(state, executor, space, selectedEnvironment, iam, ct, human).ConfigureAwait(false);
        if (!selectedEnvironment) return;
        foreach (var actor in human is { } principal ? new[] { executor, principal }.Distinct() : new[] { executor })
            if (!(await iam.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get("environments.activate"), TenantSpaceService.Scope(state.Tenant.TenantId, space), ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("environment_activation_required");
    }
}
