using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record EvaluationPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
public sealed record EvaluationAttemptSource(Guid AttemptId, Guid SuiteId, int SuiteVersion);
public sealed record DraftSuiteSaved(EvaluationSuiteVersion Suite, long DraftRevision);

public partial interface ITenantEvaluationStore
{
    ValueTask<int> RecoverExpiredEvaluationsAsync(DateTimeOffset now, int limit, CancellationToken ct = default);
    ValueTask<EvaluationGovernance> ReadEvaluationGovernanceAsync(Guid tenant, CancellationToken ct = default);
    ValueTask<bool> TrySaveEvaluationGovernanceAsync(TenantSecurityState next, long expectedSecurityRevision, EvaluationGovernance value, long expectedRevision, AuthorizationAudit audit, HumanSessionReference human, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentCore.Domain.Conversation.AgentRun>> ListPendingRunEvaluationsAsync(int limit, CancellationToken ct = default, int offset = 0, IReadOnlyList<Guid>? excludedRuns = null);
    ValueTask<EvaluationPage<EvaluationSuiteVersion>> ListSuitesAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default);
    ValueTask<EvaluationPage<ScriptVersion>> ListScriptsAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default);
    ValueTask<EvaluationSuiteVersion?> FindSuiteAsync(Guid tenant, Guid id, int? version = null, CancellationToken ct = default);
    ValueTask<ScriptVersion?> FindScriptAsync(Guid tenant, Guid id, int? version = null, CancellationToken ct = default);
    ValueTask<bool> TrySaveSuiteAsync(TenantSecurityState next, long expectedSecurityRevision, EvaluationSuiteVersion value, int expectedVersion,
        AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default);
    ValueTask<bool> TrySaveScriptAsync(TenantSecurityState next, long expectedSecurityRevision, ScriptVersion value, int expectedVersion,
        AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default);
    ValueTask<EvaluationAttempt?> FindAttemptAsync(Guid tenant, Guid id, CancellationToken ct = default);
    ValueTask<EvaluationAttempt?> FindDedupAsync(Guid tenant, Guid actor, string key, CancellationToken ct = default);
    ValueTask<IReadOnlyList<EvaluationAttemptSource>> ListAttemptSourcesAsync(Guid tenant, EvaluationSubjectKind kind, Guid subject, CancellationToken ct = default);
    ValueTask<EvaluationPage<EvaluationAttempt>> ListAttemptsAsync(Guid tenant, EvaluationSubjectKind kind, Guid subject, int page, int pageSize, CancellationToken ct = default, IReadOnlyList<Guid>? allowedAttemptIds = null);
    ValueTask<bool> TrySaveAttemptAsync(TenantSecurityState next, long expectedSecurityRevision, EvaluationAttempt value, long expectedAttemptRevision,
        AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default);
    ValueTask<DraftSuiteSaved?> TrySaveDraftSuiteAsync(TenantSecurityState next, long expectedSecurityRevision, Guid draftId, long expectedDraftRevision,
        EvaluationSuiteVersion suite, AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default);
}
