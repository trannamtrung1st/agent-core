using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantPreferenceOperationStore
{
    public async ValueTask<TenantOperationReceipt?> TryCommitPreferencesAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantPreferenceCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, next.Tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == commit.RunId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        var run = AgentRunStoreMapping.ToDomain(row);
        var existing = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == next.Tenant.TenantId && r.RunId == row.AgentRunId && r.SourceCallId == commit.SourceCallId, ct).ConfigureAwait(false);
        if (existing is not null) return TenantLocalEffectRules.MatchDirectReceipt(existing.Domain(), run, commit.SourceCallId, commit.ParametersHash);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision || run.Revision != commit.ExpectedRunRevision) return null;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        var owned = await OwnedAgents(db, tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == row.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("executor_unavailable");
        var target = Owned(owned); TenantSessionRules.RequireExecutor(before, target);
        await ValidateTenantActorAsync(db, before, target.PrincipalId, ct).ConfigureAwait(false);
        if (run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is { } requester) await ValidateTenantActorAsync(db, before, requester, ct).ConfigureAwait(false);
        var at = (clock ?? TimeProvider.System).GetUtcNow();
        var prepared = TenantApprovalRules.PreparePreferences(commit.OperationId, run.AgentInstanceId,
                AgentCore.Application.Tools.TenantPreferenceToolContract.Parameters(AgentCore.Application.Tools.TenantPreferenceToolContract.Parse(commit.ArgumentsJson)),
                target.Instance.Revision, target.Instance.PersonaRevision);
        var previous = await RootApproval(db, before.Tenant.TenantId, run.Admission.Activation.RootActivationId.ToString("D"), target.PrincipalId, prepared).AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (previous is not null) throw new AccessDeniedException(TenantApprovalRules.DuplicateReason(previous.Domain()));
        var candidate = TenantPreferenceOperationRules.Apply(run, target, commit, at);
        TenantPreferenceOperationRules.Transaction(next, expectedSecurityRevision, run, audit);
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        at = (clock ?? TimeProvider.System).GetUtcNow(); _ = TenantPreferenceOperationRules.Apply(run, target, commit, at);
        audit = TenantOperationAuditRules.WithLocalEffectResult(audit, before, run, commit.OperationId);
        var savedRun = run.RecordDirectLocalEffect(run.Revision, commit.Generation, commit.OperationId, commit.ActionHash, at);
        db.Attach(owned.Instance); owned.Instance.SettingsOverridesJson = JsonSerializer.Serialize(candidate.SettingsOverrides, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        owned.Instance.Revision = candidate.Revision; owned.Instance.UpdatedAtUtc = at.ToUnixTimeMilliseconds();
        AgentRunStoreMapping.Apply(row, savedRun);
        var receipt = new TenantOperationReceipt(commit.OperationId, null, tenant.TenantId, run.AgentRunId, run.SessionId, run.Admission.ExecutionContext.RootActivationId,
            target.PrincipalId, run.AgentInstanceId, commit.ActionHash, TenantOperationOutcome.Completed, "operation_completed", candidate.Revision, candidate.PersonaRevision,
            DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()), commit.SourceCallId, commit.ParametersHash);
        db.Add(TenantOperationReceiptRecord.From(receipt)); db.Entry(tenant).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit with { At = at });
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return receipt; }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return null; }
    }
}
