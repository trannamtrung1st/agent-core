using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

internal static class TenantCoreEventProduction
{
    internal static IReadOnlyList<EventSubscriptionSnapshot> Subscriptions(CoreEventOccurrence fact, IEnumerable<Automation> automations) =>
        automations.Where(a => a.Owner == fact.Owner && a.Status == AutomationStatus.Active
            && (a.ExpiresAtUtc is null || a.ExpiresAtUtc > fact.OccurredAtUtc))
        .SelectMany(a => a.Triggers.Where(t => t.Enabled && t.Configuration is CoreEventTrigger c && c.CoreEventKey == fact.Key)
            .Select(t => { var source = (CoreEventTrigger)t.Configuration;
                return new EventSubscriptionSnapshot(a.AutomationId, a.Owner, t.Revision, source.FilterExpression, source.Dispatch)
                    { TriggerId = t.TriggerId, PinnedSpaceId = a.PinnedSpaceId, PinnedEnvironmentId = a.PinnedEnvironmentId }; })).ToArray();

    internal static async ValueTask<AgentRun> RootAsync(AgentCoreDbContext db, AgentRun run, CancellationToken ct) =>
        await TenantExecutionLineage.RootAsync(run, async (id, token) => {
            var local = db.AgentRuns.Local.SingleOrDefault(r => r.TenantId == run.Owner.TenantId && r.AgentRunId == id.ToString("D"));
            var row = local ?? await db.AgentRuns.SingleOrDefaultAsync(r => r.TenantId == run.Owner.TenantId && r.AgentRunId == id.ToString("D"), token).ConfigureAwait(false);
            return row is null ? null : AgentRunStoreMapping.ToDomain(row);
        }, async (id, token) => {
            var row = await db.TriggerOccurrences.IgnoreQueryFilters().SingleOrDefaultAsync(o => o.TenantId == run.Owner.TenantId && o.OccurrenceId == id.ToString("D"), token).ConfigureAwait(false);
            return row is null ? null : TriggerStoreMapping.ToOccurrence(row);
        }, ct, async (id, token) => {
            var row = db.Set<DelegatedTaskRow>().Local.SingleOrDefault(t => t.TenantId == run.Owner.TenantId && t.TaskId == id)
                ?? await db.Set<DelegatedTaskRow>().SingleOrDefaultAsync(t => t.TenantId == run.Owner.TenantId && t.TaskId == id, token).ConfigureAwait(false);
            return row is null ? null : TaskPersistence.Value(row);
        }, async (id, token) => {
            var row = await db.Set<AgentMessageRow>().AsNoTracking().SingleOrDefaultAsync(m => m.TenantId == run.Owner.TenantId && m.MessageId == id, token);
            if (row is null) return null;
            var thread = await db.Set<AgentThreadRow>().AsNoTracking().SingleAsync(t => t.TenantId == row.TenantId && t.ThreadId == row.ThreadId, token);
            return new AgentCore.Application.Ports.AgentMessageResult(TaskPersistence.Value<AgentCore.Domain.Tasks.AgentThread>(thread.Json),
                TaskPersistence.Value<AgentCore.Domain.Tasks.AgentMessage>(row.Json), TaskPersistence.Value<AgentCore.Domain.Tasks.AgentMessageReceipt>(row.ReceiptJson), 0);
        }).ConfigureAwait(false);

    internal static async Task StageAsync(AgentCoreDbContext db, CoreEventOccurrence? fact, CancellationToken ct)
    {
        if (fact is null) return;
        if (fact.Owner.TenantId is not { } tenant || fact.Key == "email.message.received")
            throw new ArgumentException("A canonical Core producer requires a committed tenant source.");
        // Flush the actual source first so guards inspect committed-in-this-transaction
        // Run/Session/Instance state. The outer transaction still owns atomicity.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        // Tracking retains policy changes staged by the same Instance transaction.
        var registrations = await db.Automations.IgnoreQueryFilters().Where(a => a.TenantId == tenant
            && a.AgentInstanceId == fact.Owner.AgentInstanceId.ToString("D") && a.ExecutorPrincipalId == fact.Owner.ExecutorPrincipalId)
            .ToArrayAsync(ct).ConfigureAwait(false);
        CoreEventPersistence.Stage(db, fact, Subscriptions(fact, registrations.Select(TriggerStoreMapping.ToRegistration)));
    }

    internal static async Task StageRunAsync(AgentCoreDbContext db, AgentRun previous, AgentRun next, CancellationToken ct)
    {
        if (previous.IsTerminal || next.Status is not (AgentRunStatus.Completed or AgentRunStatus.Failed)) return;
        await StageAsync(db, CoreEventPersistence.Run(previous, next, await RootAsync(db, next, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
    }
    internal static async Task StageSessionAsync(AgentCoreDbContext db, SessionSnapshot previous, SessionSnapshot current,
        AgentCore.Application.Ports.TenantSessionRoot owner, AgentRun? producer, CancellationToken ct)
    {
        if (previous.LifecycleStatus == current.LifecycleStatus || current.LifecycleStatus is not (SessionLifecycleStatus.Completed or SessionLifecycleStatus.Ended)) return;
        if (producer is null && current.Origin.InitialBackgroundAgentRunId is { } id)
        {
            var row = await db.AgentRuns.SingleOrDefaultAsync(r => r.TenantId == owner.TenantId && r.AgentRunId == id.ToString("D"), ct).ConfigureAwait(false);
            producer = row is null ? null : AgentRunStoreMapping.ToDomain(row);
        }
        var root = producer is null ? null : await RootAsync(db, producer, ct).ConfigureAwait(false);
        await StageAsync(db, CoreEventPersistence.Session(previous, current, root,
            TriggerOwner.ForTenant(owner.TenantId, owner.AgentInstanceId, owner.ExecutorPrincipalId)), ct).ConfigureAwait(false);
    }
}
