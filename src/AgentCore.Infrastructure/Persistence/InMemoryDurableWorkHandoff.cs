using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Work;
using AgentCore.Domain.Work;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryDurableWorkHandoff : IDurableWorkHandoff
{
    private readonly InMemoryDurableState state;
    private readonly Action? beforeCommit;

    internal InMemoryDurableWorkHandoff(InMemoryDurableState state, Action? beforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        this.state = state;
        this.beforeCommit = beforeCommit;
    }

    public ValueTask<WorkItemCreateResult> AcceptAsync(
        Guid occurrenceId,
        WorkItem proposed,
        DateTimeOffset acceptedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        lock (state.Gate)
        {
            if (!state.Occurrences.TryGetValue(occurrenceId, out var occurrence))
            {
                throw AgentCoreErrors.NotFound("Occurrence was not found.");
            }

            WorkItem? existing = null;
            if (state.WorkBySource.TryGetValue(occurrence.OccurrenceId, out var existingId))
            {
                existing = state.WorkItems[existingId];
            }
            if (existing is null && state.WorkItems.ContainsKey(proposed.WorkItemId))
            {
                throw AgentCoreErrors.Conflict("Work item already exists.");
            }

            var decision = DurableWorkAcceptance.Accept(occurrence, existing, proposed, acceptedAtUtc);
            if (!decision.Changed)
            {
                return ValueTask.FromResult(decision.Result);
            }

            beforeCommit?.Invoke();
            state.Occurrences[occurrence.OccurrenceId] = decision.Occurrence;
            if (decision.Result.Kind == WorkItemCreateKind.Created)
            {
                var created = decision.Result.Item;
                state.WorkItems[created.WorkItemId] = created;
                state.WorkBySource[created.Provenance.SourceOccurrenceId] = created.WorkItemId;
            }

            return ValueTask.FromResult(decision.Result);
        }
    }
}
