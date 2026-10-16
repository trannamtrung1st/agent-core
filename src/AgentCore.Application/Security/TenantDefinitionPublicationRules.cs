using AgentCore.Application.Admin;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public sealed record TenantDefinitionEvidence(IReadOnlyList<AgentDefinitionDraftResource> Resources,
    IReadOnlyList<DefinitionEvaluationScenario> Scenarios, IReadOnlyList<DefinitionEvaluationResult> Results);

public static class TenantDefinitionPublicationRules
{
    // Shared by preparation and the transaction: resource bindings and required evidence cannot
    // be replaced by a successful structural validation or a client-supplied fingerprint.
    public static void RequireReady(AgentDefinitionDraft draft, TenantDefinitionEvidence evidence)
    {
        var blocking = DefinitionDraftResourceValidation.CollectFindings(draft.Candidate, evidence.Resources).FirstOrDefault();
        if (blocking is not null) throw new ArgumentException(blocking.Message);
        var fingerprint = DefinitionDraftConfigurationFingerprint.Compute(draft.Candidate, evidence.Resources);
        foreach (var scenario in evidence.Scenarios.Where(s => s.RequirementLevel == DefinitionEvaluationRequirementLevel.Required))
        {
            var latest = evidence.Results.Where(r => r.ScenarioId == scenario.ScenarioId).OrderByDescending(r => r.RecordedAt).FirstOrDefault();
            if (latest is null || !latest.Passed || latest.DraftRevision != draft.Revision
                || latest.ScenarioVersion != scenario.ScenarioVersion || latest.ConfigurationFingerprint != fingerprint)
                throw new ArgumentException("Required evaluation evidence is missing, stale, or failed for this draft revision.");
        }
    }
}
