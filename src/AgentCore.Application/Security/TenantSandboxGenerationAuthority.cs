using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Current authority for retaining an idle generation or observing an acknowledged background launch.
// It cannot reserve a command, renew a Run claim, initialize a generation or replay an effect.
public sealed class TenantSandboxGenerationAuthority(IIamStore security, ITenantRunStore runs, ITenantApprovalStore approvals,
    ITenantApprovedOperationStore decisions, TenantSandboxOperationTarget targets, IamAuthorizer authorizer, TimeProvider time)
{
    public async ValueTask<AgentRun> RequireAsync(SandboxLease lease, SandboxExecution execution, CancellationToken ct = default)
    {
        var tenant = lease.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var run = await runs.FindTenantRunAsync(tenant, execution.AgentRunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var request = await approvals.FindApprovalAsync(tenant, execution.ApprovalRequestId ?? Guid.Empty, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var decision = await decisions.FindApprovalDecisionAsync(tenant, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var effect = await decisions.FindOperationReceiptAsync(tenant, execution.OperationId ?? Guid.Empty, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("sandbox_launch_unconfirmed");
        var at = time.GetUtcNow();
        var source = request.RunId == run.AgentRunId ? null : await runs.FindTenantRunAsync(tenant, request.RunId, ct).ConfigureAwait(false);
        TenantRunContinuationRules.Require(run, request, source);
        TenantSandboxEffectRules.Require(request, execution, run);
        if (state.Tenant.Status != IdentityStatus.Active || run.CancellationRequested || run.Status is AgentRunStatus.Cancelled or AgentRunStatus.Failed
            || run.Owner.TenantId != tenant || run.Owner.ExecutorPrincipalId != lease.ExecutorPrincipalId || run.AgentInstanceId != lease.AgentInstanceId || run.SessionId != lease.SessionId
            || execution.Generation != lease.Generation || execution.ExecutorPrincipalId != lease.ExecutorPrincipalId || lease.ExpiresAt <= at || lease.MountAuthority is null
            || effect.OperationId != execution.OperationId || effect.RequestId != request.RequestId || effect.RunId != run.AgentRunId
            || effect.ActionHash != execution.ActionHash || effect.Outcome != TenantOperationOutcome.Completed || request.ExpiresAt <= at
            || decision.RequestId != request.RequestId || decision.TenantId != tenant || decision.Status != TenantApprovalStatus.Approved
            || decision.ActionHash != execution.ActionHash || decision.RequestRevision != request.Revision
            || run.SideEffect.Disposition != AgentRunSideEffectDisposition.Succeeded || run.SideEffect.ToolCallId != execution.ToolCallId || run.SideEffect.ActionHash != execution.ActionHash)
            throw new AccessDeniedException("sandbox_generation_authority_lost");
        if (lease.State == SandboxLeaseState.Ready)
        {
            var completed = execution.State == SandboxExecutionState.Completed
                || execution.State == SandboxExecutionState.Canceled && execution.ProcessStop?.DispatchStartedAt is not null;
            if (lease.ActiveExecutionId is not null || lease.LastExecutionId != execution.Id || !completed || !execution.TerminationConfirmed)
                throw new AccessDeniedException("sandbox_generation_authority_lost");
        }
        else if (lease.State != SandboxLeaseState.Executing || lease.ActiveExecutionId != execution.Id || !execution.Background || execution.ProviderCommandId is null
            || execution.DeadlineAt is null || execution.DeadlineAt <= at || execution.State is not (SandboxExecutionState.Running or SandboxExecutionState.Completed or SandboxExecutionState.Canceled))
            throw new AccessDeniedException("sandbox_generation_authority_lost");
        var target = await targets.ResolveAsync(run, lease.Profile.Id, ct).ConfigureAwait(false);
        var pinned = TenantSandboxOperationRules.Profile(run, lease.Profile.Id);
        TenantSessionRules.RequireExecutor(state, target.Agent);
        if (target.Lease?.Generation != lease.Generation || target.Lease.Revision != lease.Revision || target.Session.Owner.TenantId != tenant
            || target.Session.Owner.AgentInstanceId != lease.AgentInstanceId || target.Session.Owner.ExecutorPrincipalId != lease.ExecutorPrincipalId
            || target.Session.Snapshot.ProfileId is not null || target.Session.Snapshot.AgentInstanceId != lease.AgentInstanceId || target.Session.Snapshot.SessionId != lease.SessionId
            || target.Session.Snapshot.DurablyDeletedAt is not null || target.Session.Snapshot.ArchivedAt is not null
            || target.Session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || target.Session.Snapshot.Status is SessionStatus.Ended or SessionStatus.Ending
            || (pinned with { InstanceRevision = lease.Profile.InstanceRevision }) != lease.Profile
            || lease.State == SandboxLeaseState.Executing && (target.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || target.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision))
            throw new AccessDeniedException("sandbox_generation_authority_lost");
        TenantSandboxRuntimeRules.RequireCurrentPolicy(run, target.Agent, target.Baseline, pinned, target.Profile, lease.MountAuthority);
        await TenantSandboxWorkspaceRules.RequireCurrentAsync(state, run, target.Agent, target.Baseline, lease.MountAuthority.WorkspaceAccess ?? throw new AccessDeniedException("sandbox_workspace_context_required"), authorizer, ct).ConfigureAwait(false);
        await TenantSandboxApprovalRules.RequireHumanAsync(state, request, decision, authorizer, ct).ConfigureAwait(false);
        var after = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false);
        var latest = await runs.FindTenantRunAsync(tenant, run.AgentRunId, ct).ConfigureAwait(false);
        if (after?.Tenant.Revision != state.Tenant.Revision || latest?.Revision != run.Revision)
            throw new SecurityConflictException("stale_revision");
        return run;
    }
}
