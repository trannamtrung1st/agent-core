using AgentCore.Application.Security;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Evaluations;

public static class EvaluationStoreRules
{
    public static bool TryScope(AccessScope scope, out Guid id)
    {
        id = default;
        return scope.Kind == AccessScopeKind.Resource && scope.TenantId is not null
            && scope.ResourceType is "evaluation-suite" or "script" && Guid.TryParseExact(scope.ResourceId, "D", out id) && scope.ResourceId == id.ToString("D");
    }
    public static AccessScope Scope(Guid tenant, Guid id, bool script = false) => AccessScope.ForResource(tenant, script ? "script" : "evaluation-suite", id.ToString("D"));
    public static void SuiteChange(EvaluationSuiteVersion value, EvaluationSuiteVersion? before, int expected)
    {
        if ((before?.Version ?? 0) != expected || value.Version != expected + 1
            || before is not null && (before.TenantId != value.TenantId || before.SuiteId != value.SuiteId || before.DraftId != value.DraftId))
            throw new SecurityConflictException("stale_revision");
        if (value.ContentHash != EvaluationRules.Freeze(value).ContentHash) throw new ArgumentException("Suite hash does not match.");
    }
    public static void ScriptChange(ScriptVersion value, ScriptVersion? before, int expected)
    {
        if ((before?.Version ?? 0) != expected || value.Version != expected + 1 || before is not null && (before.TenantId != value.TenantId || before.ScriptId != value.ScriptId))
            throw new SecurityConflictException("stale_revision");
        if (value.ContentHash != EvaluationRules.Freeze(value).ContentHash) throw new ArgumentException("Script hash does not match.");
    }
    public static EvaluationAttempt Freeze(EvaluationAttempt value)
    {
        if (value.AttemptId == Guid.Empty || value.TenantId == Guid.Empty || value.SubjectId == Guid.Empty || value.ActorId == Guid.Empty
            || value.SuiteId == Guid.Empty || value.SuiteVersion < 1 || value.SubjectRevision < 1 || value.SourceFingerprint.Length != 64
            || value.SuiteHash.Length != 64 || string.IsNullOrWhiteSpace(value.DedupKey) || value.DedupKey.Length > 128
            || value.Findings is null || value.Findings.Count > EvaluationRules.MaxChecks || value.Revision < 1
            || !Enum.IsDefined(value.Status) || !Enum.IsDefined(value.Hook) || !Enum.IsDefined(value.SubjectKind)
            || value.Outcome is { } outcome && !Enum.IsDefined(outcome)
            || value.Findings.Any(f => f.CheckId.Length > 64 || f.ReasonCode.Length > 64 || f.Summary.Length > EvaluationRules.MaxFindingsCharacters
                || !Enum.IsDefined(f.Kind) || !Enum.IsDefined(f.Outcome))) throw new ArgumentException("Invalid evaluation attempt.");
        return value with { Findings = Array.AsReadOnly(value.Findings.ToArray()), ModelPins = value.ModelPins is null ? null : Array.AsReadOnly(value.ModelPins.ToArray()) };
    }
    public static void AttemptChange(EvaluationAttempt value, EvaluationAttempt? before, long expected, Guid actor)
    {
        if ((before?.Revision ?? 0) != expected || value.Revision != expected + 1 || value.CommitSequence is not null)
            throw new SecurityConflictException("stale_revision");
        if (before is null)
        {
            if (value.Status != EvaluationStatus.Running || value.ActorId != actor || value.Findings.Count != 0 || value.Outcome is not null)
                throw new ArgumentException("New attempts must be reserved before evaluation.");
            return;
        }
        if (before.Status is EvaluationStatus.Completed or EvaluationStatus.Cancelled or EvaluationStatus.Indeterminate
            || value with { Status = before.Status, Outcome = before.Outcome, Findings = before.Findings, UpdatedAt = before.UpdatedAt,
                Revision = before.Revision, CommitSequence = before.CommitSequence, ReasonCode = before.ReasonCode, ModelPins = before.ModelPins } != before
            || EvaluationRules.Hash(value.ModelPins) != EvaluationRules.Hash(before.ModelPins))
            throw new SecurityConflictException("evaluation_immutable");
        if (value.Status == EvaluationStatus.Completed && (value.Outcome != EvaluationRules.Aggregate(value.Findings)
            || value.Findings.Any(f => f.ReasonCode == "review_required"))) throw new ArgumentException("Final outcome must match the checks.");
        if (value.Status == EvaluationStatus.Pending && value.Findings.All(f => f.ReasonCode != "review_required"))
            throw new ArgumentException("Pending assessment requires a Manual check.");
        // Only unresolved Manual findings can change during review. Deterministic/model facts are immutable.
        if (before.Status == EvaluationStatus.Pending && (value.Findings.Count != before.Findings.Count
            || before.Findings.Where(f => f.Kind != EvaluatorKind.Manual || f.ReasonCode != "review_required").Any(f => !value.Findings.Contains(f))))
            throw new SecurityConflictException("evaluation_immutable");
    }
    public static void Findings(EvaluationAttempt value, EvaluationAttempt? before, EvaluationSuiteVersion suite, Guid actor)
    {
        if (before is null) return;
        if (value.Findings.Select(f => f.CheckId).Distinct(StringComparer.Ordinal).Count() != value.Findings.Count
            || value.Findings.Any(f => !suite.Checks.Any(c => c.CheckId == f.CheckId && c.Kind == f.Kind && c.Required == f.Required))
            || value.Status is EvaluationStatus.Completed or EvaluationStatus.Pending && value.Findings.Count != suite.Checks.Count)
            throw new ArgumentException("Findings must match the exact pinned Suite.");
        if (before.Status == EvaluationStatus.Pending)
        {
            var changed = value.Findings.Where(f => !before.Findings.Contains(f)).ToArray();
            if (changed.Length != 1 || changed[0] is not { Kind: EvaluatorKind.Manual, ReasonCode: "human_attestation", ReviewedAt: not null }
                || changed[0].ReviewerId != actor) throw new ArgumentException("Review must attribute exactly one pending Manual check to the signed-in actor.");
        }
    }
    public static void RequireScripts(EvaluationSuiteVersion suite, Func<ScriptReference, ScriptVersion?> resolve)
    {
        foreach (var reference in suite.Checks.Select(c => c.Script).OfType<ScriptReference>())
            if (resolve(reference) is not { Archived: false } script || script.TenantId != suite.TenantId || script.ContentHash != reference.ContentHash)
                throw new AccessDeniedException("script_unavailable");
    }
}
