using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantSandboxExecutionStart(bool Reserved, SandboxExecution Execution);

public sealed partial class TenantSandboxApprovedOperationService(ITenantRunStore runs, ITenantApprovalStore approvals,
    ITenantApprovedOperationStore decisions, ITenantSandboxRuntimeStore runtime, TenantSandboxOperationTarget targets,
    TenantOperationAuthorizer operations, IIdGenerator ids, TimeProvider time, ISandboxVerificationHost? host = null)
{
    public async ValueTask<SandboxExecution> StartCommandAsync(TenantRuntimeFrame frame, Guid executionId, CancellationToken ct = default)
    {
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
        var saved = await runtime.ReadTenantSandboxRuntimeAsync(tenant, frame.SessionId, executionId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        TenantRuntimeRules.Require(frame, saved.Owner, run, time.GetUtcNow());
        var execution = saved.Execution ?? throw new AccessDeniedException("resource_unavailable");
        var request = await approvals.FindApprovalAsync(tenant, execution.ApprovalRequestId ?? Guid.Empty, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("approval_unavailable");
        await targets.RequireAsync(request, run, ct, execution).ConfigureAwait(false);
        if (!await runtime.TryStartApprovedSandboxCommandAsync(new(frame, state.Tenant.Revision, run.Revision), executionId,
                execution.Revision, saved.Lease!.Revision, time.GetUtcNow(), ct).ConfigureAwait(false))
            throw new SecurityConflictException("sandbox_command_already_started");
        return (await runtime.ReadTenantSandboxRuntimeAsync(tenant, frame.SessionId, executionId, ct).ConfigureAwait(false))!.Execution!;
    }
    public async ValueTask<TenantOperationReceipt?> SettleAsync(Guid tenant, Guid session, Guid executionId, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var saved = await runtime.ReadTenantSandboxExecutionFactsAsync(tenant, session, executionId, ct).ConfigureAwait(false);
            if (saved?.Execution is not { ApprovalRequestId: not null, OperationId: not null } execution)
                throw new AccessDeniedException("resource_unavailable");
            if (TenantSandboxEffectRules.Outcome(execution) is null) return null;
            var audit = new AuthorizationAudit(ids.NewId(), tenant, saved.Owner.ExecutorPrincipalId, "sandbox.command.result",
                execution.OperationId.Value.ToString("D"), "allowed", "sandbox_command_completed", time.GetUtcNow());
            var receipt = await runtime.TrySettleApprovedSandboxExecutionAsync(tenant, session, executionId, execution.Revision, audit, ct).ConfigureAwait(false);
            if (receipt is not null) return receipt;
        }
        throw new SecurityConflictException("sandbox_execution_unsettled");
    }
    // The saved typed request supplies all command parameters. Only Reserved=true owns a new dispatch fence.
    public async ValueTask<TenantSandboxExecutionStart> ReserveAsync(TenantRuntimeFrame frame, Guid requestId, CancellationToken ct = default)
    {
        try { return await ReserveWithRaceRecoveryAsync(frame, requestId, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            if (frame.Owner.TenantId is { } tenant)
                await operations.RecordFailureAsync(new(tenant, frame.RunId), "sandbox.execute",
                    e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode,
                    ExecutionAuthorizationPurpose.EffectEligibility, ct, frame.SessionId).ConfigureAwait(false);
            throw;
        }
    }
    private async ValueTask<TenantSandboxExecutionStart> ReserveWithRaceRecoveryAsync(TenantRuntimeFrame frame, Guid requestId, CancellationToken ct)
    {
        try { return await ReserveCoreAsync(frame, requestId, ct).ConfigureAwait(false); }
        catch (SecurityConflictException e) when (e.ReasonCode is "stale_revision" or "sandbox_operation_witness_changed")
        {
            var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
            var current = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
            var saved = await runtime.ReadTenantSandboxRuntimeAsync(tenant, frame.SessionId, ct: ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            TenantRuntimeRules.Require(frame, saved.Owner, current.Run, time.GetUtcNow());
            var request = await approvals.FindApprovalAsync(tenant, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
            await targets.ResolveAsync(current.Run, request.Operation.SandboxCommand?.ProfileId ?? throw new AccessDeniedException("sandbox_approval_mismatch"), ct).ConfigureAwait(false);
            var old = await runtime.FindTenantSandboxOperationAsync(tenant, frame.SessionId, frame.RunId, "operation:" + request.Operation.OperationId.ToString("D"), ct).ConfigureAwait(false);
            if (old is null || old.ApprovalRequestId != requestId || old.OperationId != request.Operation.OperationId || old.ActionHash != request.Operation.ActionHash) throw;
            return new(false, old);
        }
    }
    private async ValueTask<TenantSandboxExecutionStart> ReserveCoreAsync(TenantRuntimeFrame frame, Guid requestId, CancellationToken ct)
    {
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
        var session = await runtime.ReadTenantSandboxRuntimeAsync(tenant, frame.SessionId, ct: ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        var request = await approvals.FindApprovalAsync(tenant, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var call = "operation:" + request.Operation.OperationId.ToString("D");
        var old = await runtime.FindTenantSandboxOperationAsync(tenant, frame.SessionId, run.AgentRunId, call, ct).ConfigureAwait(false);
        if (old is not null)
        {
            await targets.ResolveAsync(run, request.Operation.SandboxCommand?.ProfileId ?? throw new AccessDeniedException("sandbox_approval_mismatch"), ct).ConfigureAwait(false);
            if (old.ApprovalRequestId != requestId || old.OperationId != request.Operation.OperationId || old.ActionHash != request.Operation.ActionHash)
                throw new SecurityConflictException("sandbox_approval_mismatch");
            return new(false, old);
        }
        await targets.RequireAsync(request, run, ct).ConfigureAwait(false);
        var value = request.Operation.SandboxCommand ?? throw new AccessDeniedException("sandbox_approval_mismatch");
        var lease = session.Lease;
        var decision = await decisions.FindApprovalDecisionAsync(tenant, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var source = request.RunId == run.AgentRunId ? null : await runs.FindTenantRunAsync(tenant, request.RunId, ct).ConfigureAwait(false);
        var command = TenantApprovalRules.SandboxCommand(value); var at = time.GetUtcNow();
        var initializes = lease is null || lease.State == SandboxLeaseState.Terminated;
        var executionId = ids.NewId();
        SandboxLease? generation = initializes ? new(frame.SessionId, frame.Owner.AgentInstanceId, ids.NewId(), checked((lease?.Revision ?? 0) + 1),
            TenantSandboxOperationRules.Profile(run, value.ProfileId), SandboxLeaseState.Provisioning, null, at,
            at.AddSeconds(TenantSandboxRuntimeRules.InitialLifetimeSeconds(TenantSandboxOperationRules.Profile(run, value.ProfileId), command)),
            Initialization: new("Pending", value.ProfileId, value.ProfileRevision, TenantSandboxOperationRules.Profile(run, value.ProfileId).InstanceRevision),
            ActiveExecutionId: executionId, LastExecutionId: executionId, SelectedProfileId: value.ProfileId, TransitionPhase: "provisioning",
            AdmissionRunId: frame.RunId, AdmissionToolCallId: call, TenantId: tenant, ExecutorPrincipalId: frame.Owner.ExecutorPrincipalId,
            ProviderHostScope: host?.Scope ?? throw new AccessDeniedException("sandbox_host_unavailable")) : null;
        var execution = new SandboxExecution(executionId, frame.SessionId, frame.Owner.AgentInstanceId, generation?.Generation ?? lease!.Generation, run.AgentRunId, frame.Generation,
            call, request.Operation.ActionHash, SandboxExecutionState.Dispatching, at, Background: command.Background,
            CommandText: command.Command ?? JsonSerializer.Serialize(command.Argv), DeadlineAt: initializes ? null : at.AddSeconds(command.TimeoutSeconds!.Value),
            Phase: initializes ? "provisioning" : null, InitializesGeneration: initializes, CommandStartedAt: initializes ? null : at,
            ProfileId: value.ProfileId, ProfileRevision: value.ProfileRevision, TenantId: tenant, ExecutorPrincipalId: frame.Owner.ExecutorPrincipalId,
            Revision: 1, ApprovalRequestId: requestId, OperationId: request.Operation.OperationId);
        var audit = new AuthorizationAudit(ids.NewId(), tenant, frame.Owner.ExecutorPrincipalId!.Value, "sandbox.command.reserve", execution.Id.ToString("D"), "allowed", "sandbox_command_reserved", at);
        var review = new TenantSandboxApprovalReservation(new(frame, state.Tenant.Revision, run.Revision), requestId, request.Revision, decision.DecisionId, request.Operation.ActionHash, audit, generation);
        TenantSandboxApprovalRules.RequireApproved(review, request, decision, run, source, execution, at);
        try
        {
            if (await runtime.TryReserveApprovedSandboxExecutionAsync(review, execution, at, ct).ConfigureAwait(false)) return new(true, execution);
        }
        catch (SecurityConflictException e) when (e.ReasonCode == "stale_revision")
        {
            var current = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
            TenantRuntimeRules.Require(frame, session.Owner, current.Run, time.GetUtcNow());
        }
        old = await runtime.FindTenantSandboxOperationAsync(tenant, frame.SessionId, run.AgentRunId, call, ct).ConfigureAwait(false);
        return old is not null && old.ApprovalRequestId == requestId ? new(false, old) : throw new SecurityConflictException("stale_revision");
    }
}
