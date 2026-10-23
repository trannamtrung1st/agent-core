using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private AgentRun TenantCoreRoot(AgentRun run) => TenantExecutionLineage.RootAsync(run,
        (id, _) => ValueTask.FromResult(sessionBacking.ReadRetainedTenantOperationRun(run.Owner.TenantId!.Value, id)),
        (id, _) => ValueTask.FromResult(automationBacking.ReadTenantOccurrence(run.Owner.TenantId!.Value, id)),
        CancellationToken.None).AsTask().GetAwaiter().GetResult();

    // All callers hold the IAM transaction gate, so the source and this snapshot
    // become visible together. No later subscription can attach to this fact.
    private void AppendTenantCore(CoreEventOccurrence? fact)
    {
        if (fact is null) return;
        lock (automationBacking.AdmissionGate) lock (sessionBacking.CoreEvents.TenantGate)
        {
            var snapshots = TenantCoreEventProduction.Subscriptions(fact,
                automationBacking.TenantAutomations(fact.Owner.TenantId));
            sessionBacking.CoreEvents.AppendTenant(fact, snapshots);
        }
    }
    private void SaveTenantRuntimeState(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
        DateTimeOffset at, bool foreground = false)
    {
        var previous = sessionBacking.ReadTenantSession(frame.Owner.TenantId!.Value, frame.SessionId)
            ?? throw new AccessDeniedException("session_unavailable");
        var producer = sessionBacking.ReadTenantRun(frame.Owner.TenantId!.Value, frame.RunId) ?? throw new AccessDeniedException("run_unavailable");
        if (TenantRuntimeRules.PublishesResponse(producer, snapshot, id => sessionBacking.FindEntry(frame.SessionId, id)))
        {
            RequirePublicWebSources(producer, CancellationToken.None);
            RequireHttpSources(producer, CancellationToken.None);
            RequireStoredProfileSource(producer, CancellationToken.None);
            RequireStoredMemorySources(producer, CancellationToken.None);
        }
        if (TenantAutomationConversationRules.IsConversationRoot(producer)
            && TenantRuntimeRules.PublishesResponse(producer, snapshot, id => sessionBacking.FindEntry(frame.SessionId, id)))
            RequireTenantEventRead(producer, CancellationToken.None);
        CoreEventOccurrence? fact = null;
        if (previous.Snapshot.LifecycleStatus != snapshot.LifecycleStatus
            && snapshot.LifecycleStatus is SessionLifecycleStatus.Completed or SessionLifecycleStatus.Ended)
        {
            var run = sessionBacking.ReadTenantRun(frame.Owner.TenantId.Value, frame.RunId)
                ?? throw new AccessDeniedException("run_unavailable");
            lock (automationBacking.AdmissionGate)
                fact = CoreEventPersistence.Session(previous.Snapshot, snapshot, TenantCoreRoot(run),
                    TriggerOwner.ForTenant(frame.Owner.TenantId.Value, frame.Owner.AgentInstanceId, frame.Owner.ExecutorPrincipalId!.Value));
        }
        sessionBacking.SaveTenantRuntime(frame, snapshot, expectedRevision, at, foreground);
        AppendTenantCore(fact);
    }
    private void AppendTenantCoreRun(AgentRun previous, AgentRun next)
    {
        if (previous.IsTerminal || next.Status is not (AgentRunStatus.Completed or AgentRunStatus.Failed)) return;
        lock (automationBacking.AdmissionGate)
            AppendTenantCore(CoreEventPersistence.Run(previous, next, TenantCoreRoot(next)));
    }
    private void AppendTenantCoreSession(TenantSession previous, TenantSession current)
    {
        if (previous.Snapshot.LifecycleStatus == current.Snapshot.LifecycleStatus
            || current.Snapshot.LifecycleStatus is not (SessionLifecycleStatus.Completed or SessionLifecycleStatus.Ended)) return;
        var root = current.Owner;
        AgentRun? cause = null;
        var origin = current.Snapshot.Origin.InitialBackgroundAgentRunId;
        if (origin is { } id && sessionBacking.ReadRetainedTenantOperationRun(root.TenantId, id) is { } run)
            lock (automationBacking.AdmissionGate) cause = TenantCoreRoot(run);
        AppendTenantCore(CoreEventPersistence.Session(previous.Snapshot, current.Snapshot, cause,
            TriggerOwner.ForTenant(root.TenantId, root.AgentInstanceId, root.ExecutorPrincipalId)));
    }
}
