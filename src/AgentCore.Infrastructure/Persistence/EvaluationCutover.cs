using AgentCore.Application.Admin;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AgentCore.Infrastructure.Persistence;

// Installed data is converted only through the explicit stopped-host, SQLite-backup operation.
public static class EvaluationCutover
{
    public const string Recovery = "Stop the application and run: dotnet run --project src/AgentCore.Api -- migrate-evaluations <database-path> <new-backup-path>. Restore the matching database backup and previous binary if conversion fails. Do not reset tenant data.";
    public static async Task<int> MigrateAsync(string databasePath, string backupPath, CancellationToken ct = default)
    {
        databasePath = Path.GetFullPath(databasePath); backupPath = Path.GetFullPath(backupPath);
        if (!File.Exists(databasePath) || databasePath == backupPath || File.Exists(backupPath)) throw new ArgumentException("Use an existing stopped database and a new distinct backup path.");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite(new SqliteConnectionStringBuilder {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()).Options;
        await using var db = new AgentCoreDbContext(options); await db.Database.OpenConnectionAsync(ct);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray(); var known = db.Database.GetMigrations().ToArray();
        if (!applied.SequenceEqual(known.Take(applied.Length), StringComparer.Ordinal)
            || applied.LastOrDefault() is not ("20261026100000_DecisionIntelligencePreferences" or "20261027010000_UnifiedEvaluations"))
            throw new InvalidOperationException("Upgrade the complete matching predecessor schema first. " + Recovery);
        await using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
        { await backup.OpenAsync(ct); ((SqliteConnection)db.Database.GetDbConnection()).BackupDatabase(backup); }
        await db.Database.MigrateAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var roots = await db.Set<TenantDefinitionRecord>().AsNoTracking().ToDictionaryAsync(d => d.DefinitionId, ct);
        var drafts = await db.AgentDefinitionDrafts.IgnoreQueryFilters().AsNoTracking().Where(d => db.Set<TenantDefinitionRecord>().Any(t => t.DefinitionId == d.DefinitionId)).ToArrayAsync(ct);
        var converted = 0; var migratedAt = TimeProvider.System.GetUtcNow();
        var revisions = await db.Set<Tenant>().AsNoTracking().ToDictionaryAsync(t => t.TenantId, t => t.Revision, ct);
        foreach (var row in drafts)
        {
            var draft = AgentDefinitionAdminMapping.MapDraft(row); var tenant = roots[row.DefinitionId].TenantId;
            var scenarios = await db.AgentDefinitionDraftEvaluationScenarios.IgnoreQueryFilters().AsNoTracking().Where(s => s.DraftId == row.DraftId).OrderBy(s => s.ScenarioId).ToArrayAsync(ct);
            var results = await db.AgentDefinitionDraftEvaluationResults.IgnoreQueryFilters().AsNoTracking().Where(r => r.DraftId == row.DraftId).OrderBy(r => r.ResultId).ToArrayAsync(ct);
            if (scenarios.Length == 0 && results.Length == 0) continue;
            if (await db.Set<EvaluationSuiteRow>().AnyAsync(s => s.TenantId == tenant && s.SuiteId == draft.DraftId, ct))
                throw new InvalidOperationException("A partially converted draft requires the matched backup. " + Recovery);
            var checks = scenarios.Select(s => new EvaluationCheck(s.ScenarioId, s.Title, EvaluatorKind.Script, s.RequirementLevel == 0,
                (EvaluationPredicate)s.CheckType, string.IsNullOrEmpty(s.ToolName) ? null : s.ToolName, Prompt: s.Prompt)).ToArray();
            // Original suite/creator did not exist. The migration creates v1 without inventing human attribution.
            var suite = EvaluationRules.Freeze(new EvaluationSuiteVersion(tenant, draft.DraftId, 1, draft.Candidate.Identity.Name[..Math.Min(110, draft.Candidate.Identity.Name.Length)] + " checks", checks, "", Guid.Empty, migratedAt, draft.DraftId, DefinitionId: Guid.Parse(draft.DefinitionId)));
            db.Add(EvaluationPersistence.Row(suite));
            var resources = await db.AgentDefinitionDraftResources.IgnoreQueryFilters().AsNoTracking().Where(r => r.DraftId == row.DraftId).ToArrayAsync(ct);
            var fingerprint = AgentCore.Application.Security.TenantDefinitionChildRules.Fingerprint(new(draft, resources.Select(DefinitionResourcePersistence.MapDraftResource).ToArray(), [], []));
            foreach (var result in results)
            {
                var scenario = scenarios.SingleOrDefault(s => s.ScenarioId == result.ScenarioId);
                var audit = await db.Set<AuthorizationAudit>().AsNoTracking().SingleOrDefaultAsync(a => a.AuditId == Guid.Parse(result.ResultId) && a.TenantId == tenant, ct);
                var trustedOrder = result.CommitRevision is > 0 && result.CommitRevision <= revisions[tenant]
                    && audit is { Action: "agent-definitions.evaluations.run", Decision: "allowed" } && audit.Target == draft.DefinitionId && audit.At.ToUnixTimeMilliseconds() == result.RecordedAtUtc;
                var trusted = trustedOrder && ValidLegacyFindings(result.FindingsJson)
                    && result.RuntimeKind is "Synthetic" or "dca20000-0000-4000-8000-000000000001" && result.DraftRevision == draft.Revision && scenario?.ScenarioVersion == result.ScenarioVersion
                    && result.ConfigurationFingerprint == fingerprint;
                var outcome = trusted ? result.Passed ? EvaluationOutcome.Passed : EvaluationOutcome.Failed : EvaluationOutcome.Inconclusive;
                var at = DateTimeOffset.FromUnixTimeMilliseconds(result.RecordedAtUtc);
                var attempt = new EvaluationAttempt(Guid.Parse(result.ResultId), tenant, EvaluationSubjectKind.DefinitionDraft, draft.DraftId,
                    result.DraftRevision, result.ConfigurationFingerprint, suite.SuiteId, suite.Version, suite.ContentHash, EvaluationHook.Publication,
                    audit?.ActorPrincipalId ?? Guid.Empty, "migration:" + result.ResultId, EvaluationStatus.Completed, outcome,
                    [new(result.ScenarioId, EvaluatorKind.Script, scenario?.RequirementLevel == 0, outcome,
                        trusted ? "migrated_current_evidence" : "historical_requires_reevaluation", "Retained legacy check version " + result.ScenarioVersion + ". " + (trusted ? "Original exact source and trusted commit order preserved." : "Current criteria, source or trusted lineage could not be established; run a fresh evaluation."))],
                    at, at, CommitSequence: trustedOrder ? result.CommitRevision : null, ReasonCode: trusted ? "migrated" : "historical_requires_reevaluation");
                db.Add(EvaluationPersistence.Row(attempt)); converted++;
            }
            await db.AgentDefinitionDraftEvaluationResults.IgnoreQueryFilters().Where(r => r.DraftId == row.DraftId).ExecuteDeleteAsync(ct);
            await db.AgentDefinitionDraftEvaluationScenarios.IgnoreQueryFilters().Where(s => s.DraftId == row.DraftId).ExecuteDeleteAsync(ct);
        }
        await db.SaveChangesAsync(ct);
        await using (var integrity = db.Database.GetDbConnection().CreateCommand())
        {
            integrity.Transaction = tx.GetDbTransaction(); integrity.CommandText = "PRAGMA foreign_key_check";
            await using var reader = await integrity.ExecuteReaderAsync(ct); if (await reader.ReadAsync(ct)) throw new InvalidOperationException("Foreign-key check failed. " + Recovery);
        }
        await tx.CommitAsync(ct); return converted;
    }
    private static bool ValidLegacyFindings(string json)
    {
        try { var findings = System.Text.Json.JsonSerializer.Deserialize<string[]>(json); return findings is not null && findings.Length <= 100 && findings.All(f => f is not null && f.Length <= 2000); }
        catch (System.Text.Json.JsonException) { return false; }
    }

}
