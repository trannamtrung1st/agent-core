using AgentCore.Application.Admin;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public sealed record TenantDefinitionEvidence(IReadOnlyList<AgentDefinitionDraftResource> Resources,
    IReadOnlyList<DefinitionEvaluationScenario> Scenarios, IReadOnlyList<DefinitionEvaluationResult> Results);

public sealed class DefinitionPublicationNotReadyException(string reasonCode, string message) : Exception(message)
{
    public string ReasonCode { get; } = reasonCode;
}

public static class TenantDefinitionPublicationRules
{
    // Shared by preparation and the transaction: resource bindings and required evidence cannot
    // be replaced by a successful structural validation or a client-supplied fingerprint.
    public static void RequireReady(AgentDefinitionDraft draft, TenantDefinitionEvidence evidence)
    {
        var blocking = DefinitionDraftResourceValidation.CollectFindings(draft.Candidate, evidence.Resources).FirstOrDefault();
        if (blocking is not null) throw new DefinitionPublicationNotReadyException("definition_resources_invalid", "Resolve the missing or invalid definition resources before publishing.");
        var fingerprint = DefinitionDraftConfigurationFingerprint.Compute(draft.Candidate, evidence.Resources);
        foreach (var scenario in evidence.Scenarios.Where(s => s.RequirementLevel == DefinitionEvaluationRequirementLevel.Required))
        {
            var latest = evidence.Results.Where(r => r.ScenarioId == scenario.ScenarioId).OrderByDescending(r => r.CommitRevision ?? 0).ThenByDescending(r => r.RecordedAt).FirstOrDefault();
            if (latest is null) throw new DefinitionPublicationNotReadyException("evaluation_required", "Run each required evaluation before publishing.");
            if (latest.DraftRevision != draft.Revision || latest.ScenarioVersion != scenario.ScenarioVersion || latest.ConfigurationFingerprint != fingerprint)
                throw new DefinitionPublicationNotReadyException("evaluation_stale", "Run required evaluations again for the current draft.");
            if (!latest.Passed) throw new DefinitionPublicationNotReadyException("evaluation_failed", "Resolve the failed required evaluations before publishing.");
        }
    }
}
