using AgentCore.Application.Evaluations;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantEvaluationStore
{
    private readonly Dictionary<(Guid Tenant, Guid Id, int Version), EvaluationSuiteVersion> evaluationSuites = [];
    private readonly Dictionary<(Guid Tenant, Guid Id, int Version), ScriptVersion> evaluationScripts = [];
    private readonly Dictionary<(Guid Tenant, Guid Id), EvaluationAttempt> evaluationAttempts = [];

    public ValueTask<int> RecoverExpiredEvaluationsAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            var pending = evaluationAttempts.Values.Where(a => a.Status == EvaluationStatus.Running && a.CreatedAt.AddSeconds(EvaluationRules.TimeoutSeconds + 5) <= now).OrderBy(a => a.CreatedAt).Take(Math.Clamp(limit, 1, 32)).ToArray();
            foreach (var value in pending)
            {
                var state = _tenants[value.TenantId]; var sequence = state.Tenant.Revision + 1;
                evaluationAttempts[(value.TenantId, value.AttemptId)] = value with { Status = EvaluationStatus.Indeterminate, Outcome = EvaluationOutcome.Inconclusive,
                    ReasonCode = "evaluation_interrupted", UpdatedAt = now, Revision = value.Revision + 1, CommitSequence = sequence };
                _tenants[value.TenantId] = state with { Tenant = state.Tenant with { Revision = sequence } };
                _audit.Add(new(modelIdSource.NewId(), value.TenantId, value.ActorId, "evaluations.recover", value.AttemptId.ToString("D"), "recorded", "evaluation_interrupted", now));
            }
            return ValueTask.FromResult(pending.Length);
        }
    }
    private readonly Dictionary<Guid, EvaluationGovernance> evaluationGovernance = [];
    public ValueTask<EvaluationGovernance> ReadEvaluationGovernanceAsync(Guid tenant, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(EvaluationPersistence.Copy(evaluationGovernance.GetValueOrDefault(tenant) ?? new(tenant, 0, []))); }
    public ValueTask<bool> TrySaveEvaluationGovernanceAsync(TenantSecurityState next, long expectedSecurityRevision, EvaluationGovernance value, long expectedRevision, AuthorizationAudit audit, HumanSessionReference human, CancellationToken ct = default)
        => ValueTask.FromResult(WriteTenantResource(next, expectedSecurityRevision, audit, () => {
            if (value.TenantId != next.Tenant.TenantId || value.Revision != expectedRevision + 1) throw new ArgumentException("Invalid evaluation policy owner/revision.");
            if ((evaluationGovernance.GetValueOrDefault(value.TenantId)?.Revision ?? 0) != expectedRevision) return false;
            EvaluationRules.ValidateBindings(value.Defaults);
            if (value.Defaults.Any(b => b.Hook is EvaluationHook.Publication or EvaluationHook.Manual)) throw new ArgumentException("Organization defaults apply only to automatic Run hooks.");
            foreach (var binding in value.Defaults) if (Suite(value.TenantId, binding.SuiteId, binding.Version) is not { Archived: false, DraftId: null }) throw new AccessDeniedException("evaluation_suite_unavailable");
            evaluationGovernance[value.TenantId] = EvaluationPersistence.Copy(value); return true;
        }, ct, human));
    public ValueTask<IReadOnlyList<AgentCore.Domain.Conversation.AgentRun>> ListPendingRunEvaluationsAsync(int limit, CancellationToken ct = default, int offset = 0)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<AgentCore.Domain.Conversation.AgentRun>>(sessionBacking.TenantEvaluationCandidates(limit,
                run => (run.Admission.Configuration?.Definition.EvaluationBindings ?? []).Where(b => b.Hook == EvaluationHook.AfterRun || b.Hook == EvaluationHook.OnFailure && run.Status != AgentCore.Domain.Conversation.AgentRunStatus.Completed).Any(b => !evaluationAttempts.Values.Any(a => a.TenantId == run.Owner.TenantId && a.SubjectId == run.AgentRunId && a.DedupKey == TenantEvaluationService.HookKey(run, b.SuiteId, b.Version, b.Hook))), offset));
        }
    }
    public ValueTask<EvaluationSuiteVersion?> FindSuiteAsync(Guid tenant, Guid id, int? version = null, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(EvaluationPersistence.Copy(Suite(tenant, id, version))); }
    private EvaluationSuiteVersion? Suite(Guid tenant, Guid id, int? version = null) => evaluationSuites.Values
        .Where(s => s.TenantId == tenant && s.SuiteId == id && (version is null || s.Version == version)).MaxBy(s => s.Version);
    public ValueTask<ScriptVersion?> FindScriptAsync(Guid tenant, Guid id, int? version = null, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(EvaluationPersistence.Copy(Script(tenant, id, version))); }
    private ScriptVersion? Script(Guid tenant, Guid id, int? version = null) => evaluationScripts.Values
        .Where(s => s.TenantId == tenant && s.ScriptId == id && (version is null || s.Version == version)).MaxBy(s => s.Version);
    public ValueTask<EvaluationPage<EvaluationSuiteVersion>> ListSuitesAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(Page(evaluationSuites.Values.Where(s => s.TenantId == tenant && s.DraftId == null
            && (allowed is null || allowed.Contains(s.SuiteId))).GroupBy(s => s.SuiteId).Select(g => g.MaxBy(s => s.Version)!).OrderBy(s => s.SuiteId), page, pageSize));
    }
    public ValueTask<EvaluationPage<ScriptVersion>> ListScriptsAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(Page(evaluationScripts.Values.Where(s => s.TenantId == tenant
            && (allowed is null || allowed.Contains(s.ScriptId))).GroupBy(s => s.ScriptId).Select(g => g.MaxBy(s => s.Version)!).OrderBy(s => s.ScriptId), page, pageSize));
    }
    private static EvaluationPage<T> Page<T>(IEnumerable<T> source, int page, int size)
    { if (page is < 0 or > 100_000 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page)); var all = source.ToArray(); return new(EvaluationPersistence.Copy(all.Skip(page * size).Take(size).ToArray()), all.Length, page, size); }
    public ValueTask<bool> TrySaveSuiteAsync(TenantSecurityState next, long expectedSecurityRevision, EvaluationSuiteVersion value, int expectedVersion,
        AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default)
    {
        value = EvaluationRules.Freeze(value);
        return ValueTask.FromResult(WriteTenantResource(next, expectedSecurityRevision, audit, () => {
            if (value.TenantId != next.Tenant.TenantId || value.DraftId is not null) throw new ArgumentException("Use the draft transaction for draft-associated Suites.");
            var before = Suite(value.TenantId, value.SuiteId); EvaluationStoreRules.SuiteChange(value, before, expectedVersion);
            if (before is null && evaluationSuites.Values.Where(s => s.TenantId == value.TenantId && s.DraftId == null).Select(s => s.SuiteId).Distinct().Count() >= EvaluationRules.MaxCatalogs) throw new ArgumentException("Suite limit reached.");
            EvaluationStoreRules.RequireScripts(value, r => Script(value.TenantId, r.ScriptId)?.Archived == false ? Script(value.TenantId, r.ScriptId, r.Version) : null);
            evaluationSuites.Add((value.TenantId, value.SuiteId, value.Version), EvaluationPersistence.Copy(value)); return true;
        }, ct, human));
    }
    public ValueTask<bool> TrySaveScriptAsync(TenantSecurityState next, long expectedSecurityRevision, ScriptVersion value, int expectedVersion,
        AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default)
    {
        value = EvaluationRules.Freeze(value);
        return ValueTask.FromResult(WriteTenantResource(next, expectedSecurityRevision, audit, () => {
            if (value.TenantId != next.Tenant.TenantId) throw new ArgumentException("Invalid Script owner.");
            var before = Script(value.TenantId, value.ScriptId); EvaluationStoreRules.ScriptChange(value, before, expectedVersion);
            if (before is null && evaluationScripts.Values.Where(s => s.TenantId == value.TenantId).Select(s => s.ScriptId).Distinct().Count() >= EvaluationRules.MaxCatalogs) throw new ArgumentException("Script limit reached.");
            evaluationScripts.Add((value.TenantId, value.ScriptId, value.Version), EvaluationPersistence.Copy(value)); return true;
        }, ct, human));
    }
    public ValueTask<EvaluationAttempt?> FindAttemptAsync(Guid tenant, Guid id, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(EvaluationPersistence.Copy(evaluationAttempts.GetValueOrDefault((tenant, id)))); }
    public ValueTask<EvaluationAttempt?> FindDedupAsync(Guid tenant, Guid actor, string key, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(EvaluationPersistence.Copy(evaluationAttempts.Values.SingleOrDefault(a => a.TenantId == tenant && a.ActorId == actor && a.DedupKey == key))); }
    public ValueTask<IReadOnlyList<EvaluationAttemptSource>> ListAttemptSourcesAsync(Guid tenant, EvaluationSubjectKind kind, Guid subject, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult<IReadOnlyList<EvaluationAttemptSource>>(evaluationAttempts.Values.Where(a => a.TenantId == tenant && a.SubjectKind == kind && a.SubjectId == subject).Select(a => new EvaluationAttemptSource(a.AttemptId, a.SuiteId, a.SuiteVersion)).ToArray()); }
    public ValueTask<EvaluationPage<EvaluationAttempt>> ListAttemptsAsync(Guid tenant, EvaluationSubjectKind kind, Guid subject, int page, int pageSize, CancellationToken ct = default, IReadOnlyList<Guid>? allowedAttemptIds = null)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(Page(evaluationAttempts.Values.Where(a => a.TenantId == tenant && a.SubjectKind == kind && a.SubjectId == subject && (allowedAttemptIds == null || allowedAttemptIds.Contains(a.AttemptId))).OrderByDescending(a => a.CommitSequence ?? 0).ThenBy(a => a.AttemptId), page, pageSize)); }
    public ValueTask<bool> TrySaveAttemptAsync(TenantSecurityState next, long expectedSecurityRevision, EvaluationAttempt value, long expectedAttemptRevision,
        AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default)
    {
        value = EvaluationStoreRules.Freeze(value);
        return ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () => {
            if (value.TenantId != next.Tenant.TenantId) throw new ArgumentException("Invalid attempt owner.");
            var before = evaluationAttempts.GetValueOrDefault((value.TenantId, value.AttemptId));
            if ((before?.Revision ?? 0) != expectedAttemptRevision) return null;
            if (before is null && evaluationAttempts.Values.Count(a => a.TenantId == value.TenantId && a.SubjectKind == value.SubjectKind && a.SubjectId == value.SubjectId) >= 1000) throw new SecurityConflictException("evaluation_attempt_limit");
            if (before is null && evaluationAttempts.Values.Any(a => a.TenantId == value.TenantId && a.ActorId == value.ActorId && a.DedupKey == value.DedupKey)) return null;
            EvaluationStoreRules.AttemptChange(value, before, expectedAttemptRevision, audit.ActorPrincipalId);
            var suite = Suite(value.TenantId, value.SuiteId, value.SuiteVersion);
            if (suite is null || suite.ContentHash != value.SuiteHash) return null;
            EvaluationStoreRules.Findings(value, before, suite, audit.ActorPrincipalId);
            if (value.SubjectKind == EvaluationSubjectKind.DefinitionDraft)
            {
                var snapshot = EvaluationSnapshot(value.TenantId, value.SubjectId);
                if (snapshot is null || snapshot.Draft.Revision != value.SubjectRevision || TenantDefinitionChildRules.Fingerprint(snapshot) != value.SourceFingerprint) return null;
            }
            else
            {
                var run = sessionBacking.ReadTenantRun(value.TenantId, value.SubjectId);
                if (run is null || run.Revision != value.SubjectRevision || EvaluationRules.Hash(new { run.AgentRunId, run.Revision, run.Status }) != value.SourceFingerprint) return null;
            }
            var saved = value with { CommitSequence = value.Status == EvaluationStatus.Running ? null : next.Tenant.Revision };
            evaluationAttempts[(value.TenantId, value.AttemptId)] = EvaluationPersistence.Copy(saved); return saved;
        }, ct, human) is not null);
    }
    public ValueTask<DraftSuiteSaved?> TrySaveDraftSuiteAsync(TenantSecurityState next, long expectedSecurityRevision, Guid draftId, long expectedDraftRevision,
        EvaluationSuiteVersion suite, AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default)
    {
        suite = EvaluationRules.Freeze(suite);
        return ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () => {
            if (suite.TenantId != next.Tenant.TenantId || suite.DraftId != draftId || suite.SuiteId != draftId) throw new ArgumentException("Invalid draft Suite owner.");
            var draft = definitions.TenantDraft(suite.TenantId, draftId); if (draft?.Revision != expectedDraftRevision) return null;
            EvaluationStoreRules.SuiteChange(suite, Suite(suite.TenantId, draftId), suite.Version - 1);
            EvaluationStoreRules.RequireScripts(suite, r => Script(suite.TenantId, r.ScriptId)?.Archived == false ? Script(suite.TenantId, r.ScriptId, r.Version) : null);
            if (!definitions.SaveTenantDraft(suite.TenantId, draft with { Revision = draft.Revision + 1, UpdatedAt = audit.At }, draft.Revision)) return null;
            evaluationSuites.Add((suite.TenantId, suite.SuiteId, suite.Version), EvaluationPersistence.Copy(suite)); return new DraftSuiteSaved(suite, draft.Revision + 1);
        }, ct, human));
    }
    private TenantDefinitionDraftSnapshot? EvaluationSnapshot(Guid tenant, Guid draftId)
    {
        var snapshot = definitions.TenantSnapshot(tenant, draftId); if (snapshot is null) return null;
        var suites = new List<EvaluationSuiteVersion>(); if (Suite(tenant, draftId) is { } associated) suites.Add(associated);
        foreach (var binding in snapshot.Draft.Candidate.EvaluationBindings ?? [])
        {
            var suite = Suite(tenant, binding.SuiteId, binding.Version) ?? throw new AccessDeniedException("evaluation_suite_unavailable");
            if (!suites.Any(s => s.SuiteId == suite.SuiteId && s.Version == suite.Version)) suites.Add(suite);
        }
        return snapshot with { Suites = EvaluationPersistence.Copy(suites.ToArray()), Attempts = EvaluationPersistence.Copy(EvaluationPersistence.CurrentDraftEvidence(suites, evaluationAttempts.Values
            .Where(a => a.TenantId == tenant && a.SubjectKind == EvaluationSubjectKind.DefinitionDraft && a.SubjectId == draftId && a.CommitSequence > 0)
            .OrderByDescending(a => a.CommitSequence).ThenBy(a => a.AttemptId))) };
    }
    private bool OwnsEvaluation(AccessScope scope) => EvaluationStoreRules.TryScope(scope, out var id)
        && (scope.ResourceType == "script" ? Script(scope.TenantId!.Value, id) is not null : Suite(scope.TenantId!.Value, id) is not null);
}
