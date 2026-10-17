using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<bool> TryReserveApprovedSandboxExecutionAsync(TenantSandboxApprovalReservation review, SandboxExecution execution, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var write = review.Write; var tenant = write.Runtime.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var leaseRow = await db.Set<SandboxLeaseRow>().SingleOrDefaultAsync(l => l.TenantId == tenant && l.SessionId == execution.SessionId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        var lease = SandboxRuntimePersistence.Value(leaseRow);
        var run = await RequireSandboxReservation(db, write, lease.Profile, lease.MountAuthority, at, ct).ConfigureAwait(false);
        var request = (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == review.RequestId, ct).ConfigureAwait(false))?.Domain()
            ?? throw new AccessDeniedException("approval_unavailable");
        var decision = await db.Set<AgentCore.Domain.Security.TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenant && d.RequestId == request.RequestId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("approval_unavailable");
        var sourceRow = request.RunId == run.AgentRunId ? null : await OwnedRuns(db, tenant).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false);
        var source = sourceRow is null ? null : AgentRunStoreMapping.ToDomain(sourceRow);
        TenantSandboxApprovalRules.RequireApproved(review, request, decision, run, source, execution, at);
        var old = await db.Set<SandboxExecutionRow>().AsNoTracking().SingleOrDefaultAsync(e => e.OperationId == execution.OperationId, ct).ConfigureAwait(false);
        if (old is not null)
        {
            if (old.TenantId != tenant || old.ApprovalRequestId != request.RequestId || old.ApprovedActionHash != execution.ActionHash || old.SessionId != execution.SessionId.ToString("D"))
                throw new SecurityConflictException("sandbox_approval_mismatch");
            return false;
        }
        var owned = await OwnedAgents(db, tenant).SingleAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
        var agent = Owned(owned); TenantApprovalRules.Validate(request, run, agent, source: source);
        await RequireSandboxOperation(db, request, run, agent, ct).ConfigureAwait(false);
        var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
        await TenantSandboxApprovalRules.RequireHumanAsync(state, request, decision, new IamAuthorizer(this), ct).ConfigureAwait(false);
        TenantSandboxRuntimeRules.ValidateNewExecution(write, execution, lease, run, at);
        var started = run.BeginApprovedExternalEffect(run.Revision, write.Runtime.Generation, request.RequestId, request.Operation.OperationId, request.Operation.ActionHash, at);
        var runRow = await OwnedRuns(db, tenant).SingleAsync(r => r.AgentRunId == run.AgentRunId.ToString("D"), ct).ConfigureAwait(false);
        AgentRunStoreMapping.Apply(runRow, started);
        db.Add(SandboxRuntimePersistence.Row(execution));
        db.Entry(leaseRow).CurrentValues.SetValues(SandboxRuntimePersistence.Row(lease with { Revision = checked(lease.Revision + 1), State = SandboxLeaseState.Executing,
            ActiveExecutionId = execution.Id, LastExecutionId = execution.Id, AdmissionRunId = execution.AgentRunId,
            AdmissionToolCallId = execution.ToolCallId, LastActivityAt = at }));
        var audit = TenantOperationAuditRules.WithEffectResult(review.Audit, state, run, request, null);
        db.Add(audit with { Execution = audit.Execution! with { Purpose = AgentCore.Domain.Security.ExecutionAuthorizationPurpose.EffectEligibility } });
        return await SaveSandbox(db, tx, ct).ConfigureAwait(false);
    }
}
