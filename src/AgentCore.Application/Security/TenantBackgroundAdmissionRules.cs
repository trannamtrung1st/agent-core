using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Child identity comes only from an owned running parent. These primitives do not
// accept a public requester, principal, root or parent supplied by a client/model.
public static class TenantBackgroundAdmissionRules
{
    public static TenantSessionRoot Shape(TenantRuntimeFrame frame, TenantSession parentSession, AgentRun parent,
        SessionSnapshot snapshot, AgentRun child, TenantAgent agent, DateTimeOffset now)
    {
        TenantRuntimeRules.Require(frame, parentSession.Owner, parent, now);
        var previous = parent.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_context_required");
        var activation = child.Admission.Activation;
        var configuration = child.Admission.Configuration;
        if (!TenantBackgroundCreationRules.Allows(parent)
            || parent.CancellationRequested || parentSession.Snapshot.DurablyDeletedAt is not null || parentSession.Snapshot.ArchivedAt is not null
            || LifecycleTransition.IsTerminal(parentSession.Snapshot) || parentSession.Snapshot.Status is SessionStatus.Ended or SessionStatus.Ending
            || child.Owner != parent.Owner || !child.IsInitialQueued || child.AgentRunId == parent.AgentRunId || child.SessionId == parent.SessionId
            || child.Admission.OutputContract != AgentRunOutputContract.BackgroundOutcome || child.CreatedAtUtc > now || child.CreatedAtUtc < parent.CreatedAtUtc
            || activation.DedupeKey is not { Length: > 11 and <= 139 } || !activation.DedupeKey.StartsWith("background:", StringComparison.Ordinal)
            || snapshot.Entries.Count != 1 || snapshot.Entries[0].Text is not { Length: > 0 } objective
            || objective.Length > AgentRunLimits.MaxBackgroundObjectiveCharacters || snapshot.Title is not { Length: > 0 and <= 80 }
            || configuration is null || configuration.InstanceRevision != agent.Instance.Revision || configuration.PersonaRevision != agent.Instance.PersonaRevision
            || child.PinnedPersona != agent.Instance.Persona || child.DefinitionId != agent.Instance.DefinitionId || child.DefinitionVersion != agent.Instance.ActiveVersion
            || agent.PrincipalId != frame.Owner.ExecutorPrincipalId || agent.TenantId != frame.Owner.TenantId
            || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active)
            throw new AccessDeniedException("background_admission_mismatch");
        var resolved = new ResolvedAgentRunConfiguration(configuration, child.PinnedPersona, child.PinnedSkillCatalog, agent.Instance.ExecutionBudgets);
        var expected = BackgroundSessionAdmissionFactory.ForImmediate(snapshot.SessionId, snapshot.Entries[0].EntryId,
            child.ActivationId, child.AgentRunId, child.ResponseId!.Value, parentSession.Snapshot, parent,
            activation.DedupeKey[11..], objective, snapshot.Title, snapshot.Origin.ReportCompletionToOrigin,
            child.CreatedAtUtc, ExecutionBudgetResolver.Resolve(configuration.Definition, agent.Instance.ExecutionBudgets, false), resolved, child.PinnedModel);
        if (JsonSerializer.Serialize(expected.Session) != JsonSerializer.Serialize(snapshot)
            || JsonSerializer.Serialize(expected.Run.Admission) != JsonSerializer.Serialize(child.Admission)
            || child.Checkpoint is not null || child.ApprovalRequestId is not null)
            throw new AccessDeniedException("background_admission_mismatch");
        return new(previous.TenantId, child.SessionId, child.AgentInstanceId, previous.AgentPrincipalId,
            parent.Admission.OutputContract == AgentRunOutputContract.BackgroundOutcome ? previous.AgentPrincipalId
                : previous.OriginalInitiatorPrincipalId ?? throw new AccessDeniedException("requester_unavailable"), child.CreatedAtUtc);
    }

    public static ValueTask AuthorizeAsync(TenantSecurityState state, AgentRun parent, AgentDefinition current,
        IamAuthorizer authorizer, ITenantRunStore runs, CancellationToken ct) => AuthorizeAsync(state, parent, current, authorizer, runs, ct, true);
    public static ValueTask AuthorizeReportAsync(TenantSecurityState state, AgentRun parent, AgentDefinition current,
        IamAuthorizer authorizer, ITenantRunStore runs, CancellationToken ct) => AuthorizeAsync(state, parent, current, authorizer, runs, ct, false);
    private static async ValueTask AuthorizeAsync(TenantSecurityState state, AgentRun parent, AgentDefinition current,
        IamAuthorizer authorizer, ITenantRunStore runs, CancellationToken ct, bool createSession)
    {
        var execution = parent.Admission.ExecutionContext!;
        if (state.Tenant.Status != IdentityStatus.Active || state.Tenant.TenantId != execution.TenantId
            || !TenantTaskExecutionContract.Allows(parent, current) || !RolePermissions.AllowsTool(parent.Admission.Configuration!.Definition, ToolCatalog.BackgroundStart)
            || !RolePermissions.AllowsTool(current, ToolCatalog.BackgroundStart)) throw new AccessDeniedException("background_unavailable");
        foreach (var key in createSession ? new[] { "sessions.create", "agent-runs.create" } : new[] { "agent-runs.create" })
        {
            var permission = PermissionCatalog.Get(key); var scope = AccessScope.ForTenant(execution.TenantId);
            if (!(await authorizer.CheckTenantAsync(state, execution.AgentPrincipalId, permission, scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("permission_denied");
            var mode = TenantOperationPolicyService.Effective(state, permission).Mode;
            if (mode == OperationApprovalMode.Always || mode == OperationApprovalMode.WhenRequesterLacksPermission
                && execution.OriginalInitiatorPrincipalId is { } requester
                && !(await authorizer.CheckTenantAsync(state, requester, permission, scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("background_review_required");
        }
        await TenantExecutionLineage.RequireRequesterAsync(parent, state, authorizer, runs, ct).ConfigureAwait(false);
    }

    public static bool Same(AgentRun existing, SessionSnapshot saved, AgentRun proposed, SessionSnapshot candidate) =>
        existing.Owner == proposed.Owner && existing.Admission.ExecutionContext?.ParentRunId == proposed.Admission.ExecutionContext?.ParentRunId
        && existing.Admission.Activation.DedupeKey == proposed.Admission.Activation.DedupeKey
        && existing.Admission.TaskIntent?.Text == proposed.Admission.TaskIntent?.Text
        && saved.Origin.InitialTitle == candidate.Origin.InitialTitle
        && saved.Origin.ReportCompletionToOrigin == candidate.Origin.ReportCompletionToOrigin;
}
