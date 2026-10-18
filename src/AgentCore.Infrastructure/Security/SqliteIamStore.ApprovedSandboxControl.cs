using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantOperationReceipt?> TryCommitSandboxControlAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); var tenant = next.Tenant.TenantId;
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var previous = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == commit.OperationId, ct).ConfigureAwait(false);
        if (previous is not null) return TenantLocalEffectRules.MatchingReceipt(previous.Domain(), tenant, commit);
        var tenantRow = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == tenant, ct).ConfigureAwait(false);
        if (tenantRow is null || tenantRow.Revision != expectedSecurityRevision) return null;
        var before = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
        var requestRow = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == commit.RequestId, ct).ConfigureAwait(false);
        var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenant && d.RequestId == commit.RequestId, ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, tenant).SingleOrDefaultAsync(r => r.AgentRunId == commit.RunId.ToString("D"), ct).ConfigureAwait(false);
        if (requestRow is null || decision is null || row is null || row.Revision != commit.ExpectedRunRevision) return null;
        var request = requestRow.Domain(); var run = AgentRunStoreMapping.ToDomain(row);
        var sourceRow = request.RunId != run.AgentRunId ? await OwnedRuns(db, tenant).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false) : null;
        var source = sourceRow is null ? null : AgentRunStoreMapping.ToDomain(sourceRow);
        var grant = await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == tenant && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
        TenantSandboxControlEffectRules.Validate(request, decision, grant, run, commit, source);
        var owned = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false);
        if (owned is null) return null;
        var agent = Owned(owned); TenantSessionRules.RequireExecutor(before, agent);
        await ValidateTenantActorAsync(db, before, request.ExecutorPrincipalId, ct).ConfigureAwait(false);
        var at = (clock ?? TimeProvider.System).GetUtcNow();
        if (run.Claim!.LeaseExpiresAtUtc <= at) throw new SecurityConflictException("execution_claim_expired");
        TenantApprovalRules.Validate(request, run, agent, requireCurrentWitness: false, source: source);
        (SandboxLease Lease, SandboxExecution? Execution)? target = null;
        if (commit.Outcome == TenantOperationOutcome.Completed)
        {
            if (request.ExpiresAt <= at) commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = "approval_expired" };
            else
            {
                if (agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision)
                    throw new SecurityConflictException("operation_witness_changed");
                await ValidateTenantActorAsync(db, before, decision.HumanPrincipalId, ct).ConfigureAwait(false);
                if (request.OriginalInitiatorPrincipalId is { } requester) await ValidateTenantActorAsync(db, before, requester, ct).ConfigureAwait(false);
                target = await RequireSandboxControlOperation(db, request, run, agent, ct).ConfigureAwait(false);
                var session = (await ReadSandboxSession(db, tenant, request.SessionId, ct).ConfigureAwait(false))!;
                await TenantSandboxControlOperationRules.RequirePermissionsAsync(before, session.Owner, request.Operation.SandboxControl!.ProfileId,
                    decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
        }
        audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode, At = at };
        TenantSandboxControlEffectRules.Audit(next, expectedSecurityRevision, request, audit);
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        audit = TenantOperationAuditRules.WithEffectResult(audit, before, run, request, null);
        var complete = commit.Outcome == TenantOperationOutcome.Completed;
        var savedRun = run.RecordApprovedLocalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, complete, at);
        var result = new TenantOperationReceipt(commit.OperationId, request.RequestId, tenant, run.AgentRunId, run.SessionId,
            run.Admission.Activation.RootActivationId, request.ExecutorPrincipalId, agent.Instance.InstanceId, commit.ActionHash, commit.Outcome, commit.ReasonCode,
            agent.Instance.Revision, agent.Instance.PersonaRevision, DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()));
        if (complete)
        {
            var current = target!.Value;
            var pending = request.Operation.SandboxControl!.Kind == SandboxControlKind.ResetEnvironment ? TenantSandboxControlRules.Apply(current.Lease)
                : current.Lease with { Revision = checked(current.Lease.Revision + 1), SafeMessage = "Stop requested. Process cleanup is pending; canonical files are retained." };
            var leaseRow = await db.Set<SandboxLeaseRow>().SingleAsync(l => l.TenantId == tenant && l.SessionId == request.SessionId.ToString("D"), ct).ConfigureAwait(false);
            db.Entry(leaseRow).CurrentValues.SetValues(SandboxRuntimePersistence.Row(pending));
            if (request.Operation.SandboxControl.Kind == SandboxControlKind.StopProcess)
            {
                var stopped = TenantSandboxControlRules.ApplyStop(current.Execution!, audit with { AuditId = request.RequestId, ActorPrincipalId = decision.HumanPrincipalId });
                var executionRow = await db.Set<SandboxExecutionRow>().SingleAsync(e => e.TenantId == tenant && e.SessionId == leaseRow.SessionId && e.Id == stopped.Id.ToString("D"), ct).ConfigureAwait(false);
                db.Entry(executionRow).CurrentValues.SetValues(SandboxRuntimePersistence.Row(stopped));
            }
        }
        AgentRunStoreMapping.Apply(row, savedRun); db.Add(TenantOperationReceiptRecord.From(result)); db.Entry(tenantRow).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return result; }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return null; }
    }
}
