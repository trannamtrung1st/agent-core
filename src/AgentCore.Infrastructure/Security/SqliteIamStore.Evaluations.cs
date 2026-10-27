using AgentCore.Application.Evaluations;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantEvaluationStore
{
    public async ValueTask<int> RecoverExpiredEvaluationsAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pending = await db.Set<EvaluationAttemptRow>().Where(a => a.Status == EvaluationStatus.Running && a.ExpiresAtUtc <= now.ToUnixTimeMilliseconds()).OrderBy(a => a.ExpiresAtUtc).Take(Math.Clamp(limit, 1, 32)).ToArrayAsync(ct);
        foreach (var row in pending)
        {
            var state = await db.Set<Tenant>().SingleAsync(t => t.TenantId == row.TenantId, ct);
            var sequence = state.Revision + 1; db.Entry(state).CurrentValues.SetValues(state with { Revision = sequence });
            var value = EvaluationPersistence.Value(row) with { Status = EvaluationStatus.Indeterminate, Outcome = EvaluationOutcome.Inconclusive,
                ReasonCode = "evaluation_interrupted", UpdatedAt = now, Revision = row.Revision + 1, CommitSequence = sequence };
            db.Entry(row).CurrentValues.SetValues(EvaluationPersistence.Row(value));
            db.Add(new AuthorizationAudit(modelIdSource.NewId(), value.TenantId, value.ActorId, "evaluations.recover", value.AttemptId.ToString("D"), "recorded", "evaluation_interrupted", now));
        }
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return pending.Length; }
        catch (DbUpdateConcurrencyException) { return 0; }
    }
    public async ValueTask<EvaluationGovernance> ReadEvaluationGovernanceAsync(Guid tenant, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); var row = await db.Set<EvaluationGovernanceRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant, ct); return row is null ? new(tenant, 0, []) : System.Text.Json.JsonSerializer.Deserialize<EvaluationGovernance>(row.Json)!; }
    public ValueTask<bool> TrySaveEvaluationGovernanceAsync(TenantSecurityState next, long expectedSecurityRevision, EvaluationGovernance value, long expectedRevision, AuthorizationAudit audit, HumanSessionReference human, CancellationToken ct = default)
        => WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db => {
            if (value.TenantId != next.Tenant.TenantId || value.Revision != expectedRevision + 1) throw new ArgumentException("Invalid evaluation policy owner/revision.");
            var row = await db.Set<EvaluationGovernanceRow>().SingleOrDefaultAsync(r => r.TenantId == value.TenantId, ct);
            if ((row?.Revision ?? 0) != expectedRevision) return false;
            EvaluationRules.ValidateBindings(value.Defaults);
            if (value.Defaults.Any(b => b.Hook is EvaluationHook.Publication or EvaluationHook.Manual)) throw new ArgumentException("Organization defaults apply only to automatic Run hooks.");
            foreach (var binding in value.Defaults) if (await Suite(db, value.TenantId, binding.SuiteId, binding.Version, ct) is not { Archived: false, DraftId: null }) throw new AccessDeniedException("evaluation_suite_unavailable");
            if (row is null) db.Add(new EvaluationGovernanceRow { TenantId = value.TenantId, Revision = value.Revision, Json = System.Text.Json.JsonSerializer.Serialize(value) });
            else { row.Revision = value.Revision; row.Json = System.Text.Json.JsonSerializer.Serialize(value); }
            return true;
        }, ct, human);
    public async ValueTask<IReadOnlyList<AgentCore.Domain.Conversation.AgentRun>> ListPendingRunEvaluationsAsync(int limit, CancellationToken ct = default, int offset = 0, IReadOnlyList<Guid>? excludedRuns = null)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        var excluded = System.Text.Json.JsonSerializer.Serialize((excludedRuns ?? []).Take(512).Select(id => id.ToString("D")));
        var rows = await db.AgentRuns.FromSqlInterpolated($"""
            SELECT r.* FROM AgentRuns r JOIN Sessions s ON s.SessionId=r.SessionId
            WHERE NOT EXISTS(SELECT 1 FROM json_each({excluded}) x WHERE x.value=r.AgentRunId) AND r.TenantId IS NOT NULL AND r.Status IN (4,5,6) AND s.DurablyDeletedAtUtc IS NULL AND (s.RetentionDueAtUtc IS NULL OR s.RetentionDueAtUtc>{now})
              AND EXISTS(SELECT 1 FROM json_each(r.PayloadJson,'$.admission.configuration.definition.evaluationBindings') b
                WHERE (json_extract(b.value,'$.hook')=1 OR (json_extract(b.value,'$.hook')=2 AND r.Status IN (5,6)))
                AND NOT EXISTS(SELECT 1 FROM EvaluationAttempts a WHERE a.TenantId=r.TenantId AND lower(a.SubjectId)=r.AgentRunId
                  AND a.DedupKey='hook:'||r.AgentRunId||':'||json_extract(b.value,'$.suiteId')||':'||json_extract(b.value,'$.version')||':'||json_extract(b.value,'$.hook')||':'||r.Revision))
            ORDER BY r.UpdatedAtUtc,r.AgentRunId LIMIT {Math.Clamp(limit, 1, 32)} OFFSET {Math.Clamp(offset, 0, 100_000)}
            """).AsNoTracking().ToArrayAsync(ct);
        return rows.Select(AgentRunStoreMapping.ToDomain).ToArray();
    }
    public async ValueTask<EvaluationSuiteVersion?> FindSuiteAsync(Guid tenant, Guid id, int? version = null, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); return await Suite(db, tenant, id, version, ct); }
    private static async Task<EvaluationSuiteVersion?> Suite(AgentCoreDbContext db, Guid tenant, Guid id, int? version, CancellationToken ct)
    {
        var row = await db.Set<EvaluationSuiteRow>().AsNoTracking().Where(r => r.TenantId == tenant && r.SuiteId == id && (version == null || r.Version == version)).OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct);
        return row is null ? null : EvaluationPersistence.Value(row);
    }
    public async ValueTask<ScriptVersion?> FindScriptAsync(Guid tenant, Guid id, int? version = null, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); return await Script(db, tenant, id, version, ct); }
    private static async Task<ScriptVersion?> Script(AgentCoreDbContext db, Guid tenant, Guid id, int? version, CancellationToken ct)
    {
        var row = await db.Set<EvaluationScriptRow>().AsNoTracking().Where(r => r.TenantId == tenant && r.ScriptId == id && (version == null || r.Version == version)).OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct);
        return row is null ? null : EvaluationPersistence.Value(row);
    }
    public async ValueTask<EvaluationPage<EvaluationSuiteVersion>> ListSuitesAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default)
    {
        Bounds(page, pageSize); await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var query = db.Set<EvaluationSuiteRow>().AsNoTracking().Where(s => s.TenantId == tenant && s.DraftId == null && (allowed == null || allowed.Contains(s.SuiteId))
            && !db.Set<EvaluationSuiteRow>().Any(n => n.TenantId == tenant && n.SuiteId == s.SuiteId && n.Version > s.Version));
        var total = await query.CountAsync(ct); var rows = await query.OrderBy(s => s.SuiteId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct);
        await tx.CommitAsync(ct); return new(rows.Select(EvaluationPersistence.Value).ToArray(), total, page, pageSize);
    }
    public async ValueTask<EvaluationPage<ScriptVersion>> ListScriptsAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default)
    {
        Bounds(page, pageSize); await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var query = db.Set<EvaluationScriptRow>().AsNoTracking().Where(s => s.TenantId == tenant && (allowed == null || allowed.Contains(s.ScriptId))
            && !db.Set<EvaluationScriptRow>().Any(n => n.TenantId == tenant && n.ScriptId == s.ScriptId && n.Version > s.Version));
        var total = await query.CountAsync(ct); var rows = await query.OrderBy(s => s.ScriptId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct);
        await tx.CommitAsync(ct); return new(rows.Select(EvaluationPersistence.Value).ToArray(), total, page, pageSize);
    }
    private static void Bounds(int page, int size)
    { if (page is < 0 or > 100_000 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page)); }
    private static async Task RequireScripts(AgentCoreDbContext db, EvaluationSuiteVersion suite, CancellationToken ct)
    {
        foreach (var reference in suite.Checks.Select(c => c.Script).OfType<ScriptReference>())
        {
            var current = await Script(db, suite.TenantId, reference.ScriptId, null, ct);
            var pinned = await Script(db, suite.TenantId, reference.ScriptId, reference.Version, ct);
            if (current is null || current.Archived || pinned is null || pinned.Archived || pinned.ContentHash != reference.ContentHash) throw new AccessDeniedException("script_unavailable");
        }
    }
    public ValueTask<bool> TrySaveSuiteAsync(TenantSecurityState next, long expectedSecurityRevision, EvaluationSuiteVersion value, int expectedVersion,
        AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default)
    {
        value = EvaluationRules.Freeze(value);
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db => {
            if (value.TenantId != next.Tenant.TenantId || value.DraftId is not null) throw new ArgumentException("Use the draft transaction for draft-associated Suites.");
            var before = await Suite(db, value.TenantId, value.SuiteId, null, ct); EvaluationStoreRules.SuiteChange(value, before, expectedVersion);
            if (before is null && await db.Set<EvaluationSuiteRow>().Where(s => s.TenantId == value.TenantId && s.DraftId == null).Select(s => s.SuiteId).Distinct().CountAsync(ct) >= EvaluationRules.MaxCatalogs) throw new ArgumentException("Suite limit reached.");
            await RequireScripts(db, value, ct); db.Add(EvaluationPersistence.Row(value)); return true;
        }, ct, human);
    }
    public ValueTask<bool> TrySaveScriptAsync(TenantSecurityState next, long expectedSecurityRevision, ScriptVersion value, int expectedVersion,
        AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default)
    {
        value = EvaluationRules.Freeze(value);
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db => {
            if (value.TenantId != next.Tenant.TenantId) throw new ArgumentException("Invalid Script owner.");
            var before = await Script(db, value.TenantId, value.ScriptId, null, ct); EvaluationStoreRules.ScriptChange(value, before, expectedVersion);
            if (before is null && await db.Set<EvaluationScriptRow>().Where(s => s.TenantId == value.TenantId).Select(s => s.ScriptId).Distinct().CountAsync(ct) >= EvaluationRules.MaxCatalogs) throw new ArgumentException("Script limit reached.");
            db.Add(EvaluationPersistence.Row(value)); return true;
        }, ct, human);
    }
    public async ValueTask<EvaluationAttempt?> FindAttemptAsync(Guid tenant, Guid id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var row = await db.Set<EvaluationAttemptRow>().AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenant && a.AttemptId == id, ct);
        return row is null ? null : EvaluationPersistence.Value(row);
    }
    public async ValueTask<EvaluationAttempt?> FindDedupAsync(Guid tenant, Guid actor, string key, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var row = await db.Set<EvaluationAttemptRow>().AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenant && a.ActorId == actor && a.DedupKey == key, ct);
        return row is null ? null : EvaluationPersistence.Value(row);
    }
    public async ValueTask<IReadOnlyList<EvaluationAttemptSource>> ListAttemptSourcesAsync(Guid tenant, EvaluationSubjectKind kind, Guid subject, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); return await db.Set<EvaluationAttemptRow>().AsNoTracking().Where(a => a.TenantId == tenant && a.SubjectKind == kind && a.SubjectId == subject).Select(a => new EvaluationAttemptSource(a.AttemptId, a.SuiteId, a.SuiteVersion)).ToArrayAsync(ct); }
    public async ValueTask<EvaluationPage<EvaluationAttempt>> ListAttemptsAsync(Guid tenant, EvaluationSubjectKind kind, Guid subject, int page, int pageSize, CancellationToken ct = default, IReadOnlyList<Guid>? allowedAttemptIds = null)
    {
        Bounds(page, pageSize); await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var query = db.Set<EvaluationAttemptRow>().AsNoTracking().Where(a => a.TenantId == tenant && a.SubjectKind == kind && a.SubjectId == subject && (allowedAttemptIds == null || allowedAttemptIds.Contains(a.AttemptId)));
        var total = await query.CountAsync(ct); var rows = await query.OrderByDescending(a => a.CommitSequence ?? 0).ThenBy(a => a.AttemptId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct);
        await tx.CommitAsync(ct); return new(rows.Select(EvaluationPersistence.Value).ToArray(), total, page, pageSize);
    }
    public ValueTask<bool> TrySaveAttemptAsync(TenantSecurityState next, long expectedSecurityRevision, EvaluationAttempt value, long expectedAttemptRevision,
        AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default)
    {
        value = EvaluationStoreRules.Freeze(value);
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db => {
            if (value.TenantId != next.Tenant.TenantId) throw new ArgumentException("Invalid attempt owner.");
            var row = await db.Set<EvaluationAttemptRow>().SingleOrDefaultAsync(a => a.TenantId == value.TenantId && a.AttemptId == value.AttemptId, ct);
            var before = row is null ? null : EvaluationPersistence.Value(row);
            if ((before?.Revision ?? 0) != expectedAttemptRevision) return false;
            if (before is null && await db.Set<EvaluationAttemptRow>().CountAsync(a => a.TenantId == value.TenantId && a.SubjectKind == value.SubjectKind && a.SubjectId == value.SubjectId, ct) >= 1000) throw new SecurityConflictException("evaluation_attempt_limit");
            if (before is null && await db.Set<EvaluationAttemptRow>().AnyAsync(a => a.TenantId == value.TenantId && a.ActorId == value.ActorId && a.DedupKey == value.DedupKey, ct)) return false;
            EvaluationStoreRules.AttemptChange(value, before, expectedAttemptRevision, audit.ActorPrincipalId);
            var suite = await Suite(db, value.TenantId, value.SuiteId, value.SuiteVersion, ct);
            if (suite is null || suite.ContentHash != value.SuiteHash) return false;
            var nativeIds = value.Findings.Select(f => f.SandboxExecutionId).OfType<Guid>().ToArray();
            var nativeRows = await db.Set<EvaluationSandboxRow>().Where(r => r.TenantId == value.TenantId && nativeIds.Contains(r.ExecutionId)).ToArrayAsync(ct);
            var nativeFacts = nativeRows.Select(EvaluationSandboxPersistence.Value).ToDictionary(r => r.ExecutionId);
            EvaluationStoreRules.Findings(value, before, suite, audit.ActorPrincipalId, id => nativeFacts.GetValueOrDefault(id));
            if (value.SubjectKind == EvaluationSubjectKind.DefinitionDraft)
            {
                var snapshot = await DefinitionSnapshot(db, value.TenantId, value.SubjectId, ct);
                if (snapshot is null || snapshot.Draft.Revision != value.SubjectRevision || TenantDefinitionChildRules.Fingerprint(snapshot) != value.SourceFingerprint) return false;
            }
            else
            {
                var key = value.SubjectId.ToString("D");
                var runRow = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == key && r.TenantId == value.TenantId, ct);
                var run = runRow is null ? null : AgentRunStoreMapping.ToDomain(runRow);
                if (run is null || !EvaluationStoreRules.MatchesRunOrigin(value, run) || run.Revision != value.SubjectRevision || EvaluationRules.Hash(new { run.AgentRunId, run.Revision, run.Status }) != value.SourceFingerprint) return false;
            }
            var saved = value with { CommitSequence = value.Status == EvaluationStatus.Running ? null : next.Tenant.Revision };
            if (row is null) db.Add(EvaluationPersistence.Row(saved)); else db.Entry(row).CurrentValues.SetValues(EvaluationPersistence.Row(saved));
            return true;
        }, ct, human);
    }
    public ValueTask<DraftSuiteSaved?> TrySaveDraftSuiteAsync(TenantSecurityState next, long expectedSecurityRevision, Guid draftId, long expectedDraftRevision,
        EvaluationSuiteVersion suite, AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default)
    {
        suite = EvaluationRules.Freeze(suite);
        return WriteDefinitionAsync(next, expectedSecurityRevision, audit, async db => {
            if (suite.TenantId != next.Tenant.TenantId || suite.DraftId != draftId || suite.SuiteId != draftId) throw new ArgumentException("Invalid draft Suite owner.");
            var draft = await Draft(db, suite.TenantId, draftId).SingleOrDefaultAsync(ct); if (draft?.Revision != expectedDraftRevision) return null;
            EvaluationStoreRules.SuiteChange(suite, await Suite(db, suite.TenantId, draftId, null, ct), suite.Version - 1);
            await RequireScripts(db, suite, ct); draft.Revision++; draft.UpdatedAtUtc = audit.At.ToUnixTimeMilliseconds();
            db.Add(EvaluationPersistence.Row(suite)); return new DraftSuiteSaved(suite, draft.Revision);
        }, ct, human);
    }
    private static async Task<bool> OwnsEvaluation(AgentCoreDbContext db, AccessScope scope, CancellationToken ct)
    {
        if (!EvaluationStoreRules.TryScope(scope, out var id)) return false;
        return scope.ResourceType == "script" ? await db.Set<EvaluationScriptRow>().AnyAsync(s => s.TenantId == scope.TenantId && s.ScriptId == id, ct)
            : await db.Set<EvaluationSuiteRow>().AnyAsync(s => s.TenantId == scope.TenantId && s.SuiteId == id, ct);
    }
}
