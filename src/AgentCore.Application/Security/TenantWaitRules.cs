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

public static class TenantWaitRules
{
    public static void RequireSession(TenantSession session)
    {
        var snapshot = session.Snapshot;
        if (snapshot.DurablyDeletedAt is not null || snapshot.ArchivedAt is not null || SessionLifecycle.IsTerminal(snapshot.LifecycleStatus)
            || snapshot.Status is SessionStatus.Ended or SessionStatus.Ending
            || snapshot.Status == SessionStatus.Paused && !SessionPauseSemantics.IsTransportResumable(snapshot.PauseReason))
            throw new AccessDeniedException("wait_unavailable");
    }
    public static bool Allows(AgentRun run) => TenantTaskExecutionContract.Allows(run);

    public static void Require(AgentRun run, AgentDefinition current, AgentRunCommand.SuspendWait suspend)
    {
        if (!Allows(run) || !RolePermissions.AllowsTool(run.Admission.Configuration!.Definition, ToolCatalog.ExecutionWait)
            || !RolePermissions.AllowsTool(current, ToolCatalog.ExecutionWait)) throw new AccessDeniedException("wait_unavailable");
        suspend.Wait.Validate();
        if (run.Checkpoint is null || JsonSerializer.Serialize(run.Checkpoint) != JsonSerializer.Serialize(suspend.Value)
            || !AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages))
            throw AgentCoreErrors.Conflict("Wait requires the current pending tool checkpoint.");
        var pending = AgentRunToolCallCheckpoint.PendingCalls(messages!);
        var call = pending.SingleOrDefault(c => c.Id == suspend.Wait.ToolCallId && c.Name == ToolCatalog.ExecutionWait)
            ?? throw AgentCoreErrors.Conflict("Wait tool call is not pending.");
        using var json = JsonDocument.Parse(call.ArgumentsJson);
        var expected = ExecutionWaitContract.Parse(call.Id, json.RootElement, suspend.AtUtc);
        if (expected.Mode != suspend.Wait.Mode || expected.Until != suspend.Wait.Until || expected.StartedAtUtc != suspend.Wait.StartedAtUtc
            || expected.DeadlineUtc != suspend.Wait.DeadlineUtc || !expected.BackgroundSessionIds.SequenceEqual(suspend.Wait.BackgroundSessionIds)
            || suspend.Wait.SuspendedGeneration is not null)
            throw AgentCoreErrors.Conflict("Wait differs from its saved tool call.");
    }

    public static async ValueTask<BackgroundCompletionCandidate?> ChildAsync(AgentRun parent, TenantSession? child,
        Func<Guid, CancellationToken, ValueTask<AgentRun?>> read, CancellationToken ct)
    {
        if (child is null || child.Owner.TenantId != parent.Owner.TenantId || child.Owner.AgentInstanceId != parent.AgentInstanceId
            || child.Owner.ExecutorPrincipalId != parent.Owner.ExecutorPrincipalId || child.Snapshot.DurablyDeletedAt is not null
            || child.Snapshot.Origin.OriginatingSessionId != parent.SessionId || child.Snapshot.SessionId == parent.SessionId
            || child.Snapshot.Origin.InitialBackgroundAgentRunId is not { } initialId) return null;
        var initial = await read(initialId, ct).ConfigureAwait(false);
        if (initial is null || initial.Owner != parent.Owner || initial.SessionId != child.Owner.SessionId
            || initial.Admission.ExecutionContext?.Origin != ExecutionOrigin.ImmediateBackground
            || initial.Admission.Activation.SourceSessionId != parent.SessionId
            || initial.Admission.Activation.SourceAgentRunId != child.Snapshot.Origin.OriginatingAgentRunId
            || initial.Admission.OutputContract != AgentRunOutputContract.BackgroundOutcome) return null;
        var current = initial; var seen = new HashSet<Guid>();
        for (var depth = 0; depth < 64; depth++)
        {
            if (!seen.Add(current.AgentRunId)) return null;
            if (current.Result?.OutcomeKind != AgentRunOutcomeKind.Continued)
                return new(initial, child.Snapshot, current);
            if (current.Result.ContinuationRunId is not { } nextId || await read(nextId, ct).ConfigureAwait(false) is not { } next) return null;
            try { TenantBackgroundLineage.RequireHandoff(current, next); }
            catch (AccessDeniedException) { return null; }
            current = next;
        }
        return null;
    }

    public static async ValueTask<bool> CanReadAsync(TenantSecurityState state, AgentRun parent, BackgroundCompletionCandidate child,
        IamAuthorizer authorizer, CancellationToken ct)
    {
        var executor = parent.Owner.ExecutorPrincipalId!.Value; var tenant = parent.Owner.TenantId!.Value;
        foreach (var (key, scope) in new[] { ("sessions.read", TenantSessionRules.Scope(tenant, child.Run.SessionId)),
            ("agent-runs.read", TenantRunRules.Scope(tenant, child.Run.AgentRunId)),
            ("agent-runs.read", TenantRunRules.Scope(tenant, (child.CurrentTaskRun ?? child.Run).AgentRunId)) })
        {
            var permission = PermissionCatalog.Get(key);
            if (!(await authorizer.CheckOwnedTenantAsync(state, executor, permission, scope, ct).ConfigureAwait(false)).Allowed) return false;
            var policy = TenantOperationPolicyService.Effective(state, permission);
            if (policy.Mode == OperationApprovalMode.Always || policy.Mode == OperationApprovalMode.WhenRequesterLacksPermission
                && parent.Admission.ExecutionContext?.OriginalInitiatorPrincipalId is { } requester
                && !(await authorizer.CheckOwnedTenantAsync(state, requester, permission, scope, ct).ConfigureAwait(false)).Allowed) return false;
        }
        return true;
    }
}
