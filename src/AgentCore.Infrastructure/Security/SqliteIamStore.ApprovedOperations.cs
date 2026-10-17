using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantApprovedOperationStore
{
    public async ValueTask<TenantApprovalDecision?> FindApprovalDecisionAsync(Guid tenantId, Guid requestId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenantId && d.RequestId == requestId, ct).ConfigureAwait(false);
    }
    public async ValueTask<TenantOperationReceipt?> FindOperationReceiptAsync(Guid tenantId, Guid operationId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.OperationId == operationId, ct).ConfigureAwait(false))?.Domain();
    }

    public async ValueTask<TenantOperationReceipt?> TryCommitLocalEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var existing = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == commit.OperationId, ct).ConfigureAwait(false);
        if (existing is not null) return TenantLocalEffectRules.MatchingReceipt(existing.Domain(), next.Tenant.TenantId, commit);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision) return null;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        var requestRow = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == commit.RequestId, ct).ConfigureAwait(false);
        var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == commit.RequestId, ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == commit.RunId.ToString("D"), ct).ConfigureAwait(false);
        if (requestRow is null || decision is null || row is null || row.Revision != commit.ExpectedRunRevision) return null;
        var request = requestRow.Domain(); var run = AgentRunStoreMapping.ToDomain(row);
        var sourceRow = request.RunId != run.AgentRunId ? await OwnedRuns(db, request.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false) : null;
        var source = sourceRow is null ? null : AgentRunStoreMapping.ToDomain(sourceRow);
        var grant = await db.Set<TenantOperationGrant>().SingleOrDefaultAsync(g => g.TenantId == tenant.TenantId && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
        TenantLocalEffectRules.Validate(request, decision, grant, run, commit, source);
        var owned = await OwnedAgents(db, tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false);
        if (owned is null) return null;
        var target = Owned(owned); TenantSessionRules.RequireExecutor(before, target);
        await ValidateTenantActorAsync(db, before, request.ExecutorPrincipalId, ct).ConfigureAwait(false);
        var at = (clock ?? TimeProvider.System).GetUtcNow();
        commit = TenantLocalEffectRules.AtCommit(commit, request, grant, run, target, at, source);
        var correlated = await RootApproval(db, request.TenantId, request.RootActivationId.ToString("D"), request.ExecutorPrincipalId, request.Operation).AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (correlated is not null && correlated.RequestId != request.RequestId)
            commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = "root_operation_conflict" };
        if (commit.Outcome == TenantOperationOutcome.Completed)
        {
            try { await ValidateTenantActorAsync(db, before, decision.HumanPrincipalId, ct).ConfigureAwait(false); }
            catch (AccessDeniedException) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = "grantor_authority_lost" }; }
            if (commit.Outcome == TenantOperationOutcome.Completed && request.OriginalInitiatorPrincipalId is { } requester)
                try { await ValidateTenantActorAsync(db, before, requester, ct).ConfigureAwait(false); }
                catch (AccessDeniedException) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = "requester_unavailable" }; }
        }
        audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode, At = at };
        TenantLocalEffectRules.ValidateTransaction(next, expectedSecurityRevision, request, audit);
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        at = (clock ?? TimeProvider.System).GetUtcNow();
        commit = TenantLocalEffectRules.AtCommit(commit, request, grant, run, target, at, source);
        audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode, At = at };
        audit = TenantOperationAuditRules.WithLocalEffectResult(audit, before, run, commit.OperationId, request.RequestId, grant?.GrantId);
        var complete = commit.Outcome == TenantOperationOutcome.Completed;
        var updated = complete ? TenantAgentConfigurationRules.Apply(target.Instance,
            TenantLocalEffectRules.Change(request, target.Instance), at) : target.Instance;
        var savedRun = run.RecordApprovedLocalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, complete, at);
        if (complete)
        {
            db.Attach(owned.Instance);
            if (request.Operation.Kind == TenantOperationKind.Persona)
            {
                owned.Instance.PersonaJson = JsonSerializer.Serialize(updated.Persona, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                owned.Instance.PersonaRevision = updated.PersonaRevision;
            }
            else owned.Instance.SettingsOverridesJson = JsonSerializer.Serialize(updated.SettingsOverrides, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            owned.Instance.Revision = updated.Revision; owned.Instance.UpdatedAtUtc = at.ToUnixTimeMilliseconds();
        }
        AgentRunStoreMapping.Apply(row, savedRun);
        if (grant is not null) db.Entry(grant).CurrentValues.SetValues(grant with { Status = complete ? OperationGrantStatus.Consumed : OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) });
        var receipt = new TenantOperationReceipt(commit.OperationId, request.RequestId, request.TenantId, run.AgentRunId, run.SessionId,
            run.Admission.Activation.RootActivationId, request.ExecutorPrincipalId, target.Instance.InstanceId, commit.ActionHash, commit.Outcome, commit.ReasonCode,
            updated.Revision, updated.PersonaRevision, DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()));
        db.Add(TenantOperationReceiptRecord.From(receipt)); db.Entry(tenant).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return receipt; }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return null; }
    }
}
