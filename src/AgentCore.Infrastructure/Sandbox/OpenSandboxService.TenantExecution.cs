using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private ITenantSandboxRuntimeStore TenantRuntime => tenantRuntime ?? throw new AccessDeniedException("tenant_effect_unavailable");
    private TenantSandboxExecutionAuthority TenantExecution => tenantExecutionAuthority ?? throw new AccessDeniedException("tenant_effect_unavailable");
    private ValueTask<AgentRun?> TenantRuntimeRun(Guid tenant, Guid run, CancellationToken ct) =>
        (tenantRuns ?? throw new AccessDeniedException("tenant_effect_unavailable")).FindTenantRunAsync(tenant, run, ct);
    private async ValueTask<SandboxLease?> ReadLeaseAsync(SandboxLease owner, CancellationToken ct) => owner.TenantId is { } tenant
        ? (await TenantRuntime.ReadTenantSandboxExecutionFactsAsync(tenant, owner.SessionId, owner.ActiveExecutionId ?? owner.LastExecutionId ?? Guid.Empty, ct))?.Lease
        : await store.GetLeaseAsync(owner.SessionId, ct);
    private async ValueTask<SandboxExecution?> ReadReceiptAsync(SandboxLease owner, Guid execution, CancellationToken ct) => owner.TenantId is { } tenant
        ? (await TenantRuntime.ReadTenantSandboxExecutionFactsAsync(tenant, owner.SessionId, execution, ct))?.Execution
        : await store.GetExecutionAsync(owner.SessionId, execution, ct);

    public async ValueTask<SandboxCommandResult> ExecuteApprovedAsync(TenantRuntimeFrame frame, Guid requestId, CancellationToken ct = default)
    {
        if (!options.Enabled) throw new AccessDeniedException("sandbox_host_unavailable");
        var gate = Gate(frame.SessionId);
        if (!await gate.WaitAsync(0, ct)) throw new SecurityConflictException("sandbox_execution_busy");
        try
        {
            var started = await (approvedSandbox ?? throw new AccessDeniedException("tenant_effect_unavailable")).ReserveAsync(frame, requestId, ct);
            var tenant = frame.Owner.TenantId!.Value;
            var saved = await TenantRuntime.ReadTenantSandboxRuntimeAsync(tenant, frame.SessionId, started.Execution.Id, ct)
                ?? throw new AccessDeniedException("resource_unavailable");
            var lease = saved.Lease ?? throw new AccessDeniedException("resource_unavailable");
            var receipt = saved.Execution ?? throw new AccessDeniedException("resource_unavailable");
            if (!started.Reserved)
            {
                await approvedSandbox.SettleAsync(tenant, frame.SessionId, receipt.Id, ct);
                return FinalizedResult(lease, receipt); // Observe or settle retained facts; never dispatch again.
            }
            try
            {
                var current = await TenantExecution.RequireAsync(frame, receipt.Id, ct);
                if (current.Request.RequestId != requestId) throw new SecurityConflictException("sandbox_approval_mismatch");
                var command = TenantApprovalRules.SandboxCommand(current.Request.Operation.SandboxCommand!);
                lease = current.Lease; receipt = current.Execution;
                // The existing watcher continuously checks the current, renewable claim. The saved command deadline stays fixed.
                var remaining = receipt.DeadlineAt!.Value - time.GetUtcNow();
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                var ceiling = TimeSpan.FromSeconds(Math.Min(command.TimeoutSeconds!.Value, lease.Profile.MaxCommandSeconds));
                if (remaining > ceiling) remaining = ceiling;
                using var timeout = new CancellationTokenSource(remaining, time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                await CheckMountedAuthorityAsync(lease, linked.Token);
                ValidateCwd(lease, command.Cwd);
                await using var sdk = await ConnectAsync(lease, linked.Token);
                lease = await ReserveExecutionLifetimeAsync(sdk, lease, Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)), linked.Token);
                await TenantExecution.RequireAsync(frame, receipt.Id, linked.Token);
                receipt = await SaveReceiptAsync(receipt with { Phase = "dispatch", FilesystemEffectsMayHaveOccurred = true }, false, linked.Token);
                return await ExecuteReservedCommandAsync(sdk, lease, receipt, command, current.Run.Admission.Configuration!.Definition,
                    async token => { await TenantExecution.RequireAsync(frame, receipt.Id, token); }, linked.Token);
            }
            catch (Exception error) when (error is not FinalizationCrash)
            {
                receipt = await ReadReceiptAsync(lease, receipt.Id, CancellationToken.None) ?? receipt;
                lease = await ReadLeaseAsync(lease, CancellationToken.None) ?? lease;
                if (!receipt.FilesystemEffectsMayHaveOccurred)
                {
                    receipt = await SaveReceiptAsync(receipt with { State = SandboxExecutionState.Canceled, Phase = "notDispatched",
                        FinishedAt = time.GetUtcNow(), TerminationConfirmed = true, FailureCategory = error is OperationCanceledException ? "cancellation" : "authorityOrSetupChanged" }, false, CancellationToken.None);
                    lease = await SaveAsync(lease with { State = SandboxLeaseState.Ready, ActiveExecutionId = null }, CancellationToken.None);
                }
                else if (!receipt.TerminationConfirmed)
                    (lease, receipt) = await AbortExecutionFinalizationAsync(lease, receipt, error);
                return FinalizedResult(lease, receipt);
            }
            finally
            {
                using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
                await approvedSandbox.SettleAsync(tenant, frame.SessionId, receipt.Id, settlement.Token);
                ct.ThrowIfCancellationRequested();
            }
        }
        finally { gate.Release(); }
    }
}
