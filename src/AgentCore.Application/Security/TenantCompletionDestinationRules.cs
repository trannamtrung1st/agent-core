using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

// A destination is an exact causal witness, never authority inherited from its creator.
public static class TenantCompletionDestinationRules
{
    public static bool Configured(TenantBackgroundCompletion source) => TenantTaskExecutionContract.IsIndependent(source.InitialChild ?? source.Child);
    public static bool Eligible(TenantSession? target, Guid tenant, Guid instance, Guid executor) => target is not null
        && target.Owner.TenantId == tenant && target.Owner.AgentInstanceId == instance && target.Owner.ExecutorPrincipalId == executor
        && target.Snapshot.Origin.Kind == SessionOriginKind.UserChat && (target.Snapshot.Surfaces & SessionSurface.ChatList) != 0
        && target.Snapshot.DurablyDeletedAt is null && target.Snapshot.ArchivedAt is null && !LifecycleTransition.IsTerminal(target.Snapshot)
        && target.Snapshot.LifecycleStatus == SessionLifecycleStatus.Active && target.Snapshot.Status is not (SessionStatus.Ended or SessionStatus.Ending)
        && (target.Snapshot.Status != SessionStatus.Paused || SessionPauseSemantics.IsTransportResumable(target.Snapshot.PauseReason));

    public static void Binding(AgentRun initial, TriggerOccurrence? occurrence, Guid destination)
    {
        var context = initial.Admission.ExecutionContext!;
        if (!TenantTaskExecutionContract.IsIndependent(initial) || occurrence is null
            || occurrence.Owner != TriggerOwner.ForTenant(context.TenantId, initial.AgentInstanceId, context.AgentPrincipalId)
            || occurrence.Disposition != OccurrenceRoutingDisposition.AcceptedDurable || occurrence.AcceptedAgentRunId != initial.AgentRunId
            || occurrence.ExecutionSessionId != initial.SessionId || occurrence.OccurrenceId != initial.Admission.Activation.TriggerOccurrenceId
            || occurrence.AutomationId != TenantAutomationRules.SourceAutomation(initial)
            || occurrence.ExecutionTarget != AutomationExecutionTarget.Background || occurrence.CompletionDelivery.SessionId != destination
            || initial.Admission.Activation.SourceSessionId is not null || initial.Admission.Activation.SourceAgentRunId is not null
            || context.ParentRunId is not null || context.ParentActivationId is not null || context.RootActivationId != initial.ActivationId)
            throw new AccessDeniedException("completion_destination_changed");
    }

    public static bool Allows(TenantBackgroundCompletion source, AgentRun root, AgentDefinition current) =>
        TenantTaskExecutionContract.Allows(root, current) && (Configured(source)
            || RolePermissions.AllowsTool(root.Admission.Configuration!.Definition, ToolCatalog.BackgroundStart) && RolePermissions.AllowsTool(current, ToolCatalog.BackgroundStart));

    public static IEnumerable<(string Key, AccessScope Scope)> Communication(Guid tenant, Guid instance, Guid destination) =>
        new[] { ("sessions.read", TenantSessionRules.Scope(tenant, destination)), ("agents.chat", AccessScope.ForResource(tenant, "agent", instance.ToString("D"))) };

    public static async ValueTask AuthoringAsync(TenantSecurityState state, Guid human, Automation automation, TenantSession? target, IamAuthorizer iam, CancellationToken ct)
    {
        if (automation.CompletionDelivery.SessionId is not { } destination) return;
        if (!Eligible(target, state.Tenant.TenantId, automation.Owner.AgentInstanceId, automation.Owner.ExecutorPrincipalId!.Value)
            || target!.Owner.SessionId != destination) throw new AccessDeniedException("completion_destination_unavailable");
        foreach (var (key, scope) in Communication(state.Tenant.TenantId, automation.Owner.AgentInstanceId, destination))
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct)).Allowed)
                throw new AccessDeniedException("completion_destination_unavailable");
    }

    public static async ValueTask CommunicationAsync(TenantSecurityState state, TenantBackgroundCompletion source, IamAuthorizer iam, CancellationToken ct, bool requireNoReview = false)
    {
        if (!Configured(source)) return;
        foreach (var (key, scope) in Communication(state.Tenant.TenantId, source.Child.AgentInstanceId, source.Parent!.Owner.SessionId))
        {
            if (!(await iam.CheckOwnedTenantAsync(state, source.Child.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get(key), scope, ct)).Allowed)
                throw new AccessDeniedException("completion_destination_permission_required");
            if (requireNoReview && await TenantSandboxReadOperationRules.NeedsReviewAsync(state,
                source.Child.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, key, scope, iam, ct))
                throw new AccessDeniedException("background_review_required");
        }
    }

    public static async ValueTask<bool> NeedsCommunicationReviewAsync(TenantSecurityState state, TenantBackgroundCompletion source, IamAuthorizer iam, CancellationToken ct)
    {
        if (!Configured(source)) return false;
        foreach (var (key, scope) in Communication(state.Tenant.TenantId, source.Child.AgentInstanceId, source.Parent!.Owner.SessionId))
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, source.Child.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, key, scope, iam, ct)) return true;
        return false;
    }
}
