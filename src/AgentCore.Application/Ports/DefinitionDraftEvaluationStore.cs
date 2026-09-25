using AgentCore.Application.Admin;

namespace AgentCore.Application.Ports;

public interface IDefinitionDraftEvaluationStore
{
    ValueTask<IReadOnlyList<DefinitionEvaluationScenario>> ListScenariosAsync(
        Guid draftId,
        CancellationToken cancellationToken = default);

    ValueTask<DefinitionEvaluationScenario> UpsertScenarioWithRevisionBumpAsync(
        Guid draftId,
        long expectedRevision,
        DefinitionEvaluationScenarioUpsert upsert,
        CancellationToken cancellationToken = default);

    ValueTask RemoveScenarioWithRevisionBumpAsync(
        Guid draftId,
        long expectedRevision,
        string scenarioId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DefinitionEvaluationResult>> ListResultsAsync(
        Guid draftId,
        CancellationToken cancellationToken = default);

    ValueTask<DefinitionEvaluationResult?> GetLatestResultAsync(
        Guid draftId,
        string scenarioId,
        CancellationToken cancellationToken = default);

    ValueTask<DefinitionEvaluationResult> SaveResultAsync(
        DefinitionEvaluationResult result,
        CancellationToken cancellationToken = default);
}
