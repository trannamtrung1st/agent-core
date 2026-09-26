using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed class AgentDefinitionDraftEvaluationService(
    AgentDefinitionLifecycleService lifecycle,
    AgentDefinitionResourceService resources,
    IDefinitionDraftEvaluationStore store,
    IDefinitionResourceContentStore contentStore,
    AgentDefinitionDraftSyntheticEvaluationRunner evaluationRunner,
    TimeProvider time)
{
    public async ValueTask<IReadOnlyList<DefinitionEvaluationScenario>> ListScenariosAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        _ = await lifecycle.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        return await store.ListScenariosAsync(draftId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DefinitionEvaluationScenario> UpsertScenarioAsync(
        long expectedRevision,
        DefinitionEvaluationScenarioUpsert upsert,
        CancellationToken cancellationToken = default)
    {
        DefinitionEvaluationScenarioValidator.Validate(upsert);
        _ = await lifecycle.GetDraftAsync(upsert.DraftId, cancellationToken).ConfigureAwait(false);
        return await store.UpsertScenarioWithRevisionBumpAsync(
            upsert.DraftId,
            expectedRevision,
            upsert,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RemoveScenarioAsync(
        Guid draftId,
        long expectedRevision,
        string scenarioId,
        CancellationToken cancellationToken = default)
    {
        _ = await lifecycle.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        await store.RemoveScenarioWithRevisionBumpAsync(draftId, expectedRevision, scenarioId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DefinitionEvaluationResult>> ListResultsAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        _ = await lifecycle.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        return await store.ListResultsAsync(draftId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DefinitionEvaluationResult> RunScenarioAsync(
        Guid draftId,
        string scenarioId,
        CancellationToken cancellationToken = default)
    {
        var draft = await lifecycle.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        var revisionSnapshot = draft.Revision;
        var scenarios = await store.ListScenariosAsync(draftId, cancellationToken).ConfigureAwait(false);
        var scenario = scenarios.SingleOrDefault(item =>
            string.Equals(item.ScenarioId, scenarioId, StringComparison.Ordinal))
            ?? throw AgentCoreErrors.NotFound("Evaluation scenario was not found.");

        var draftResources = await resources.ListDraftResourcesAsync(draftId, cancellationToken).ConfigureAwait(false);
        draft = await lifecycle.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (draft.Revision != revisionSnapshot)
        {
            throw AgentCoreErrors.Conflict("Draft changed during evaluation; retry evaluation.");
        }

        var fingerprint = DefinitionDraftConfigurationFingerprint.Compute(draft.Candidate, draftResources);
        var snapshots = await LoadResourceSnapshotsAsync(draftId, draftResources, cancellationToken)
            .ConfigureAwait(false);
        var (passed, findings) = await evaluationRunner
            .RunAsync(draft.Candidate, snapshots, scenario, cancellationToken)
            .ConfigureAwait(false);
        var result = new DefinitionEvaluationResult(
            draft.DraftId,
            draft.Revision,
            fingerprint,
            scenario.ScenarioId,
            scenario.ScenarioVersion,
            "synthetic-offline",
            passed,
            findings,
            time.GetUtcNow());
        return await store.SaveResultAsync(result, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask EnsureRequiredEvidenceAsync(
        Guid draftId,
        long expectedRevision,
        string configurationFingerprint,
        CancellationToken cancellationToken = default)
    {
        var scenarios = await store.ListScenariosAsync(draftId, cancellationToken).ConfigureAwait(false);
        var required = scenarios
            .Where(scenario => scenario.RequirementLevel == DefinitionEvaluationRequirementLevel.Required)
            .ToArray();
        if (required.Length == 0)
        {
            return;
        }

        foreach (var scenario in required)
        {
            var latest = await store.GetLatestResultAsync(draftId, scenario.ScenarioId, cancellationToken)
                .ConfigureAwait(false);
            if (latest is null
                || latest.DraftRevision != expectedRevision
                || !string.Equals(latest.ConfigurationFingerprint, configurationFingerprint, StringComparison.Ordinal)
                || !latest.Passed
                || latest.ScenarioVersion != scenario.ScenarioVersion)
            {
                throw AgentCoreErrors.Validation(
                    "Required evaluation evidence is missing, stale, or failed for this draft revision.");
            }
        }
    }

    private async ValueTask<IReadOnlyList<DefinitionDraftSyntheticResourceSnapshot>> LoadResourceSnapshotsAsync(
        Guid draftId,
        IReadOnlyList<AgentDefinitionDraftResource> draftResources,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<DefinitionDraftSyntheticResourceSnapshot>(draftResources.Count);
        foreach (var resource in draftResources)
        {
            var bytes = await contentStore.ReadAsync(resource.ContentSha256, cancellationToken).ConfigureAwait(false)
                ?? throw AgentCoreErrors.Validation(
                    $"Draft resource '{resource.LogicalPath}' content is missing for evaluation.");
            snapshots.Add(new DefinitionDraftSyntheticResourceSnapshot(
                resource.LogicalPath,
                resource.Kind,
                resource.ContentSha256,
                bytes));
        }

        return snapshots;
    }
}
