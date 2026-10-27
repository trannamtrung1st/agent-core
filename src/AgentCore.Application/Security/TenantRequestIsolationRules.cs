using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Security;

public static class TenantRequestIsolationRules
{
    public static void AutomationRoot(SessionSnapshot session, AgentRun proposed, IReadOnlyList<AgentRun> previous)
    {
        try { NewRoot(session, proposed, previous); }
        catch (AgentCoreException error) when (error.Code == "fresh_session_required")
        { throw new AccessDeniedException("automation_conversation_unavailable"); }
    }
    public static bool Strict(SessionSnapshot session, IReadOnlyList<AgentRun> runs) => RequestIsolation.Required(session.Definition)
        || runs.Any(r => r.Admission.Configuration is { } pin && RequestIsolation.Required(pin.Definition));
    public static void NewRoot(SessionSnapshot session, AgentRun proposed, IReadOnlyList<AgentRun> previous)
    {
        if (previous.Count > 0 && (Strict(session, previous) || proposed.Admission.Configuration is { } pin && RequestIsolation.Required(pin.Definition)))
            throw RequestIsolation.FreshSessionRequired();
    }
    public static bool Ready(TenantSession parent, IReadOnlyList<AgentRun> runs,
        IReadOnlyList<TenantSession> children, IReadOnlyList<AgentRun> childRuns, IReadOnlyList<CompletionInboxItem> receipts,
        IReadOnlyList<DelegatedTask>? tasks = null, IReadOnlyList<TaskNotification>? taskNotifications = null)
    {
        if (parent.Snapshot.ProfileId is not null || parent.Snapshot.DurablyDeletedAt is not null || parent.Snapshot.ArchivedAt is not null
            || parent.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || parent.Snapshot.PendingAgentInputIds.Count > 0
            || runs.Count is 0 or > 100 || children.Count > 100 || childRuns.Count > 1000 || !Strict(parent.Snapshot, runs)) return false;
        return Settled(new(parent, runs, children, childRuns, receipts) { Tasks = tasks ?? [], TaskNotifications = taskNotifications ?? [] });
    }
    public static bool Settled(TenantSessionSettlement graph)
    {
        var (parent, runs, children, childRuns, receipts, complete) = graph;
        if (!complete || parent.Snapshot.ProfileId is not null || parent.Snapshot.PendingAgentInputIds.Count > 0
            || runs.Count > 100 || children.Count > 100 || childRuns.Count > 1000 || receipts.Count > 1000) return false;
        static bool Unsettled(AgentRun r) => !r.IsTerminal || r.SideEffect.Disposition is AgentRunSideEffectDisposition.Prepared
            or AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate;
        if (graph.Tasks.Count > 100 || graph.TaskNotifications.Count > 1000) return false;
        foreach (var task in graph.Tasks)
        {
            if (task.Source.TenantId != parent.Owner.TenantId || task.Source.SessionId != parent.Owner.SessionId || !TaskLimits.Terminal(task.State)) return false;
            var notice = graph.TaskNotifications.SingleOrDefault(n => n.TaskId == task.TaskId && n.TenantId == parent.Owner.TenantId
                && n.RecipientSessionId == parent.Owner.SessionId && n.TerminalRunId == task.TerminalRunId);
            if (notice is null) return false;
            if (task.Request.ReportOnCompletion && notice.WithheldReason is null
                && (notice.HandlingRunId is null || !runs.Any(r => r.IsTerminal && !Unsettled(r)
                    && r.Admission.TaskReport?.NotificationId == notice.NotificationId))) return false;
        }
        if (runs.Any(r => r.Owner != AgentRunOwner.ForTenant(parent.Owner.TenantId, parent.Owner.AgentInstanceId, parent.Owner.ExecutorPrincipalId)
            || r.SessionId != parent.Owner.SessionId || Unsettled(r))) return false;
        foreach (var child in children)
        {
            if (child.Owner.TenantId != parent.Owner.TenantId || child.Snapshot.Origin.OriginatingSessionId != parent.Owner.SessionId) return false;
            var initial = childRuns.SingleOrDefault(r => r.SessionId == child.Owner.SessionId && r.AgentRunId == child.Snapshot.Origin.InitialBackgroundAgentRunId);
            if (initial?.Admission.ExecutionContext is null) return false;
            var own = childRuns.Where(r => r.SessionId == child.Owner.SessionId && r.Admission.ExecutionContext?.RootActivationId == initial.Admission.ExecutionContext.RootActivationId).ToArray();
            if (own.Length == 0 || own.Any(r => r.Owner != AgentRunOwner.ForTenant(child.Owner.TenantId, child.Owner.AgentInstanceId, child.Owner.ExecutorPrincipalId))) return false;
            if (own.Any(Unsettled)) return false;
            if (child.Snapshot.Origin.Kind == SessionOriginKind.DelegatedTask)
            {
                if (!graph.Tasks.Any(t => t.WorkerSessionId == child.Owner.SessionId && t.InitialWorkerRunId == initial.AgentRunId
                    && t.Assignment.InstanceId == child.Owner.AgentInstanceId && t.Assignment.PrincipalId == child.Owner.ExecutorPrincipalId)) return false;
            }
            else if (child.Snapshot.Origin.ReportCompletionToOrigin)
            {
                // Continued children account their final successor, rather than the initial Continued outcome.
                var final = own.Where(r => r.IsTerminal && r.Result?.OutcomeKind != AgentRunOutcomeKind.Continued).ToArray();
                if (final.Length == 0 || final.Any(r => !receipts.Any(receipt => receipt.Owner == r.Owner
                    && receipt.ParentSessionId == parent.Owner.SessionId && receipt.ChildAgentRunId == r.AgentRunId
                    && receipt.Status is CompletionInboxStatus.Delivered or CompletionInboxStatus.Skipped))) return false;
            }
        }
        return true;
    }
    public static bool LeaseOwner(TenantSessionRoot owner, SandboxLease lease) => lease.TenantId == owner.TenantId
        && lease.SessionId == owner.SessionId && lease.AgentInstanceId == owner.AgentInstanceId && lease.ExecutorPrincipalId == owner.ExecutorPrincipalId;
}
