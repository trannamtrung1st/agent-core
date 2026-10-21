using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantOperationReceipt?> TryCommitTenantExperienceAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantExperienceCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, next.Tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == commit.RunId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        var existing = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == next.Tenant.TenantId && r.RunId == row.AgentRunId && r.SourceCallId == commit.SourceCallId, ct).ConfigureAwait(false);
        if (existing is not null) return TenantLocalEffectRules.MatchDirectReceipt(existing.Domain(), run, commit.SourceCallId, commit.ParametersHash);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision || run.Revision != commit.ExpectedRunRevision) return null;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        var observed = await ObserveExperienceOperation(db, run, TenantExperienceToolContract.Parse(commit.Prepared.ToolName, commit.Prepared.ArgumentsJson), commit.Prepared, null, ct).ConfigureAwait(false);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var at = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        var prepared = TenantExperienceCommitRules.Require(observed, commit, now); var iam = new IamAuthorizer(this);
        await TenantExperienceRules.RawAsync(observed.Reader, observed.Source!.Witness, false, iam, ct, manage: true).ConfigureAwait(false);
        if (await TenantSandboxReadOperationRules.NeedsReviewAsync(before, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, "memory.manage", AccessScope.ForResource(before.Tenant.TenantId, "agent", run.AgentInstanceId.ToString("D")), iam, ct).ConfigureAwait(false)) throw new AccessDeniedException("experience_review_required");
        var previous = await RootApproval(db, before.Tenant.TenantId, run.Admission.Activation.RootActivationId.ToString("D"), observed.Reader.Agent.PrincipalId, prepared).AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (previous is not null) throw new AccessDeniedException(TenantApprovalRules.DuplicateReason(previous.Domain()));
        TenantExperienceCommitRules.Transaction(next, expectedSecurityRevision, run, audit);
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        var values = TenantExperienceOperationRules.Values(observed, at);
        if (values.Count > 0)
        {
            var owner = values[^1].OwnerKey;
            if (await db.Set<TenantExperienceRecord>().CountAsync(i => i.TenantId == tenant.TenantId && i.AgentInstanceId == row.AgentInstanceId && i.OwnerKey == owner && i.Visibility != (int)ExperienceVisibility.Deleted, ct).ConfigureAwait(false) >= TenantExperienceRules.MaxRetained)
                throw new AccessDeniedException("experience_capacity");
            foreach (var value in values) db.Add(TenantExperienceRecord.Row(value));
        }
        var savedRun = run.RecordDirectLocalEffect(run.Revision, commit.Generation, commit.OperationId, commit.ActionHash, (clock ?? TimeProvider.System).GetUtcNow()); AgentRunStoreMapping.Apply(row, savedRun);
        var receipt = new TenantOperationReceipt(commit.OperationId, null, tenant.TenantId, run.AgentRunId, run.SessionId, run.Admission.ExecutionContext!.RootActivationId,
            observed.Reader.Agent.PrincipalId, run.AgentInstanceId, commit.ActionHash, TenantOperationOutcome.Completed, TenantExperienceRules.Recorded,
            observed.Reader.Agent.Instance.Revision, observed.Reader.Agent.Instance.PersonaRevision, at, commit.SourceCallId, commit.ParametersHash);
        db.Add(TenantOperationReceiptRecord.From(receipt)); db.Entry(tenant).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(TenantOperationAuditRules.WithLocalEffectResult(audit, before, run, commit.OperationId) with { At = at });
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return receipt; }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return null; }
    }
    private async Task<IReadOnlyList<AgentCore.Domain.Experience.TenantExperience>> RequireExperienceEffect(AgentCoreDbContext db, TenantApprovalRequest request, AgentCore.Domain.Conversation.AgentRun run, Guid human, DateTimeOffset at, CancellationToken ct)
    {
        var value = request.Operation.ExperienceOperation!;
        var observed = await ObserveExperienceOperation(db, run, TenantExperienceToolContract.Parse(value.ToolName, value.ArgumentsJson), value, null, ct).ConfigureAwait(false);
        await TenantExperienceConsentRules.ExecutorAsync(request, observed, new IamAuthorizer(this), ct).ConfigureAwait(false);
        await TenantExperienceConsentRules.ReviewerAsync(request, observed, human, new IamAuthorizer(this), ct).ConfigureAwait(false);
        var values = TenantExperienceOperationRules.Values(observed, DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()));
        if (values.Count > 0)
        {
            var owner = values[^1].OwnerKey;
            var retained = await db.Set<TenantExperienceRecord>().CountAsync(i => i.TenantId == request.TenantId && i.AgentInstanceId == run.AgentInstanceId.ToString("D") && i.OwnerKey == owner && i.Visibility != (int)ExperienceVisibility.Deleted, ct).ConfigureAwait(false);
            if (retained >= TenantExperienceRules.MaxRetained) throw new AccessDeniedException("experience_capacity");
        }
        return values;
    }

    private async Task<TenantExperienceOperationObservation> RequireExperienceApproval(AgentCoreDbContext db, TenantApprovalRequest request, AgentCore.Domain.Conversation.AgentRun run, CancellationToken ct)
    {
        var value = request.Operation.ExperienceOperation!;
        var observed = await ObserveExperienceOperation(db, run, TenantExperienceToolContract.Parse(value.ToolName, value.ArgumentsJson), value, null, ct).ConfigureAwait(false);
        await TenantExperienceConsentRules.ExecutorAsync(request, observed, new IamAuthorizer(this), ct).ConfigureAwait(false);
        return observed;
    }

}
