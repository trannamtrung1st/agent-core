using System.Collections.Concurrent;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Definitions;

public sealed class InMemoryDefinitionDraftEvaluationStore : IDefinitionDraftEvaluationStore
{
    private readonly IAgentDefinitionAdminStore admin;
    private readonly ConcurrentDictionary<Guid, DraftEvaluationState> _drafts = new();

    public InMemoryDefinitionDraftEvaluationStore(IAgentDefinitionAdminStore admin)
    {
        this.admin = admin;
        if (admin is InMemoryAgentDefinitionAdminStore inMemory)
        {
            inMemory.EvaluationStore = this;
        }
    }

    internal void DeleteDraftEvaluation(Guid draftId) =>
        _drafts.TryRemove(draftId, out _);

    public ValueTask<IReadOnlyList<DefinitionEvaluationScenario>> ListScenariosAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_drafts.TryGetValue(draftId, out var state))
        {
            return ValueTask.FromResult<IReadOnlyList<DefinitionEvaluationScenario>>([]);
        }

        lock (state.Gate)
        {
            return ValueTask.FromResult<IReadOnlyList<DefinitionEvaluationScenario>>(
                state.Scenarios.Values.OrderBy(item => item.ScenarioId, StringComparer.Ordinal).ToArray());
        }
    }

    public async ValueTask<DefinitionEvaluationScenario> UpsertScenarioWithRevisionBumpAsync(
        Guid draftId,
        long expectedRevision,
        DefinitionEvaluationScenarioUpsert upsert,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registryGate = DefinitionDraftLockRegistry.For(draftId);
        lock (registryGate)
        {
            var draft = admin.GetDraftAsync(draftId, cancellationToken).AsTask().GetAwaiter().GetResult()
                ?? throw AgentCoreErrors.NotFound("Definition draft was not found.");
            if (draft.Revision != expectedRevision)
            {
                throw AgentCoreErrors.Conflict("Draft revision is stale.");
            }

            _ = admin.BumpDraftRevisionAsync(
                new AgentDefinitionDraftRevisionBump(draftId, expectedRevision, upsert.UpdatedAt),
                cancellationToken).AsTask().GetAwaiter().GetResult();

            var state = _drafts.GetOrAdd(draftId, _ => new DraftEvaluationState());
            lock (state.Gate)
            {
                var nextVersion = state.Scenarios.TryGetValue(upsert.ScenarioId, out var existing)
                    ? existing.ScenarioVersion + 1
                    : 1;
                var scenario = new DefinitionEvaluationScenario(
                    upsert.DraftId,
                    upsert.ScenarioId,
                    nextVersion,
                    upsert.Title,
                    upsert.Prompt,
                    upsert.RequirementLevel,
                    upsert.CheckType,
                    upsert.ToolName,
                    upsert.UpdatedAt);
                state.Scenarios[upsert.ScenarioId] = scenario;
                return scenario;
            }
        }
    }

    public async ValueTask RemoveScenarioWithRevisionBumpAsync(
        Guid draftId,
        long expectedRevision,
        string scenarioId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registryGate = DefinitionDraftLockRegistry.For(draftId);
        lock (registryGate)
        {
            var draft = admin.GetDraftAsync(draftId, cancellationToken).AsTask().GetAwaiter().GetResult()
                ?? throw AgentCoreErrors.NotFound("Definition draft was not found.");
            if (draft.Revision != expectedRevision)
            {
                throw AgentCoreErrors.Conflict("Draft revision is stale.");
            }

            if (!_drafts.TryGetValue(draftId, out var state))
            {
                throw AgentCoreErrors.NotFound("Evaluation scenario was not found.");
            }

            lock (state.Gate)
            {
                if (!state.Scenarios.ContainsKey(scenarioId))
                {
                    throw AgentCoreErrors.NotFound("Evaluation scenario was not found.");
                }
            }

            _ = admin.BumpDraftRevisionAsync(
                new AgentDefinitionDraftRevisionBump(draftId, expectedRevision, DateTimeOffset.UtcNow),
                cancellationToken).AsTask().GetAwaiter().GetResult();

            lock (state.Gate)
            {
                if (!state.Scenarios.Remove(scenarioId))
                {
                    throw AgentCoreErrors.NotFound("Evaluation scenario was not found.");
                }
            }
        }
    }

    public ValueTask<IReadOnlyList<DefinitionEvaluationResult>> ListResultsAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_drafts.TryGetValue(draftId, out var state))
        {
            return ValueTask.FromResult<IReadOnlyList<DefinitionEvaluationResult>>([]);
        }

        lock (state.Gate)
        {
            return ValueTask.FromResult<IReadOnlyList<DefinitionEvaluationResult>>(
                state.Results.Values
                    .SelectMany(items => items)
                    .OrderByDescending(item => item.RecordedAt)
                    .ToArray());
        }
    }

    public ValueTask<DefinitionEvaluationResult?> GetLatestResultAsync(
        Guid draftId,
        string scenarioId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_drafts.TryGetValue(draftId, out var state))
        {
            return ValueTask.FromResult<DefinitionEvaluationResult?>(null);
        }

        lock (state.Gate)
        {
            if (!state.Results.TryGetValue(scenarioId, out var items) || items.Count == 0)
            {
                return ValueTask.FromResult<DefinitionEvaluationResult?>(null);
            }

            return ValueTask.FromResult<DefinitionEvaluationResult?>(items[^1]);
        }
    }

    public ValueTask<DefinitionEvaluationResult> SaveResultAsync(
        DefinitionEvaluationResult result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _drafts.GetOrAdd(result.DraftId, _ => new DraftEvaluationState());
        lock (state.Gate)
        {
            if (!state.Results.TryGetValue(result.ScenarioId, out var items))
            {
                items = [];
                state.Results[result.ScenarioId] = items;
            }

            items.Add(result);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class DraftEvaluationState
    {
        internal object Gate { get; } = new();

        internal Dictionary<string, DefinitionEvaluationScenario> Scenarios { get; } =
            new(StringComparer.Ordinal);

        internal Dictionary<string, List<DefinitionEvaluationResult>> Results { get; } =
            new(StringComparer.Ordinal);
    }
}
