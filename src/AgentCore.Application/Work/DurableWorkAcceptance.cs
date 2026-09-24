using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Work;

namespace AgentCore.Application.Work;

public sealed record DurableAcceptance(WorkItemCreateResult Result, TriggerOccurrence Occurrence, bool Changed);

public static class DurableWorkAcceptance
{
    public static DurableAcceptance Accept(
        TriggerOccurrence occurrence,
        WorkItem? existing,
        WorkItem proposed,
        DateTimeOffset acceptedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(proposed);
        if (proposed.Provenance.SourceOccurrenceId != occurrence.OccurrenceId)
        {
            throw AgentCoreErrors.Validation("Work item source occurrence does not match.");
        }

        if (!SameOwner(occurrence.Owner, proposed.Owner))
        {
            throw AgentCoreErrors.Validation("Work item owner does not match the occurrence.");
        }

        if (occurrence.Disposition == OccurrenceRoutingDisposition.AcceptedDurable)
        {
            if (existing is null
                || occurrence.DurableWorkItemId != existing.WorkItemId
                || existing.Provenance.SourceOccurrenceId != occurrence.OccurrenceId)
            {
                throw AgentCoreErrors.Persistence("Accepted durable occurrence is missing its work item.");
            }

            return new DurableAcceptance(
                new WorkItemCreateResult(WorkItemCreateKind.Existing, existing),
                occurrence,
                false);
        }

        if (occurrence.Disposition != OccurrenceRoutingDisposition.AwaitingDurableWork)
        {
            throw AgentCoreErrors.Validation("Occurrence is not awaiting durable work.");
        }

        WorkItem item;
        WorkItemCreateKind kind;
        if (existing is not null)
        {
            if (!SameOwner(occurrence.Owner, existing.Owner)
                || existing.Provenance.SourceOccurrenceId != occurrence.OccurrenceId)
            {
                throw AgentCoreErrors.Conflict("Source occurrence is already owned.");
            }

            item = existing;
            kind = WorkItemCreateKind.Existing;
        }
        else
        {
            if (!proposed.IsInitialQueued)
            {
                throw AgentCoreErrors.Validation("Only a new queued work item can be accepted.");
            }

            item = proposed;
            kind = WorkItemCreateKind.Created;
        }

        var accepted = occurrence.WithDurableAcceptance(item.WorkItemId, occurrence.RoutingRevision + 1, acceptedAtUtc);
        return new DurableAcceptance(new WorkItemCreateResult(kind, item), accepted, true);
    }

    private static bool SameOwner(TriggerOwner trigger, WorkOwner work) =>
        trigger.AgentInstanceId == work.AgentInstanceId && trigger.ProfileId == work.ProfileId;
}
