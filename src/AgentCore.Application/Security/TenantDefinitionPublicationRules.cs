using AgentCore.Application.Admin;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Evaluations;

namespace AgentCore.Application.Security;

public sealed record TenantDefinitionEvidence(IReadOnlyList<AgentDefinitionDraftResource> Resources,
    IReadOnlyList<EvaluationSuiteVersion> Suites, IReadOnlyList<EvaluationAttempt> Attempts);

public sealed class DefinitionPublicationNotReadyException(string reasonCode, string message) : Exception(message)
{
    public string ReasonCode { get; } = reasonCode;
}

public static class TenantDefinitionPublicationRules
{
    public static void RequireReady(AgentDefinitionDraft draft, TenantDefinitionEvidence evidence)
    {
        var blocking = DefinitionDraftResourceValidation.CollectFindings(draft.Candidate, evidence.Resources).FirstOrDefault();
        if (blocking is not null) throw new DefinitionPublicationNotReadyException("definition_resources_invalid", "Resolve the missing or invalid definition resources before publishing.");
        var fingerprint = DefinitionDraftConfigurationFingerprint.Compute(draft.Candidate, evidence.Resources);
        foreach (var suite in evidence.Suites.Where(s => s.DraftId == draft.DraftId || (draft.Candidate.EvaluationBindings ?? []).Any(b => b.SuiteId == s.SuiteId && b.Version == s.Version && b.Policy == EvaluationPolicy.RequirePassBeforePublication)))
        {
            foreach (var check in suite.Checks.Where(c => c.Required))
            {
                var latest = evidence.Attempts.Where(a => a.SuiteId == suite.SuiteId && a.CommitSequence is > 0 && a.Findings.Any(f => f.CheckId == check.CheckId))
                    .OrderByDescending(a => a.CommitSequence).ThenBy(a => a.AttemptId).FirstOrDefault();
                if (latest is null) throw new DefinitionPublicationNotReadyException("evaluation_required", "Run each required evaluation before publishing.");
                if (latest.SubjectRevision != draft.Revision || latest.SuiteVersion != suite.Version || latest.SuiteHash != suite.ContentHash || latest.SourceFingerprint != fingerprint)
                    throw new DefinitionPublicationNotReadyException("evaluation_stale", "Run required evaluations again for the current draft.");
                var finding = latest.Findings.SingleOrDefault(f => f.CheckId == check.CheckId);
                if (finding is null) throw new DefinitionPublicationNotReadyException("evaluation_inconclusive", "Every required evaluation needs a conclusive current pass.");
                if (finding.Outcome == EvaluationOutcome.Failed) throw new DefinitionPublicationNotReadyException("evaluation_failed", "Resolve the failed required evaluations before publishing.");
                if (latest.Status is not (EvaluationStatus.Completed or EvaluationStatus.Pending) || finding.Outcome != EvaluationOutcome.Passed)
                    throw new DefinitionPublicationNotReadyException("evaluation_inconclusive", "Every required evaluation needs a conclusive current pass.");
            }
        }
    }
    public static AgentDefinition FreezeDraftSuite(AgentDefinition payload, IReadOnlyList<EvaluationSuiteVersion> suites, Guid draftId)
    {
        var associated = suites.SingleOrDefault(s => s.DraftId == draftId);
        if (associated is null) return payload;
        var bindings = (payload.EvaluationBindings ?? []).Where(b => b.SuiteId != draftId).Append(new EvaluationBinding(associated.SuiteId, associated.Version,
            EvaluationHook.Publication, EvaluationPolicy.RequirePassBeforePublication)).ToArray();
        return payload with { EvaluationBindings = bindings };
    }
}
