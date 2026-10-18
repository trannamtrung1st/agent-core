using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Sessions;
using OpenSandbox.Models;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private async ValueTask<SandboxCommandResult> ExecuteReservedCommandAsync(SdkSandbox sdk, SandboxLease lease,
        SandboxExecution receipt, SandboxCommandRequest command, AgentDefinition definition, Func<CancellationToken, ValueTask> fence, CancellationToken ct)
    {
        var remaining = receipt.DeadlineAt - time.GetUtcNow();
        var ceiling = Math.Min(command.TimeoutSeconds ?? lease.Profile.MaxCommandSeconds, lease.Profile.MaxCommandSeconds);
        var timeout = (int)Math.Max(1, Math.Min(ceiling, Math.Ceiling(remaining?.TotalSeconds ?? ceiling)));
        var stdout = new BoundedText(32768); var stderr = new BoundedText(32768);
        var lastOutputSave = time.GetUtcNow().AddSeconds(-1);
        async Task SaveOutputAsync()
        {
            if (time.GetUtcNow() - lastOutputSave < TimeSpan.FromSeconds(1)) return;
            lastOutputSave = time.GetUtcNow();
            receipt = receipt with { Stdout = stdout.Text, Stderr = stderr.Text, Truncated = stdout.Truncated || stderr.Truncated };
            receipt = await SaveReceiptAsync(receipt, false, ct);
        }
        try
        {
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("The saved command deadline elapsed before dispatch.");
            await fence(ct);
            var handlers = new ExecutionHandlers {
                SkipAccumulation = true,
                OnStdout = async m => { stdout.Append(m.Text); await SaveOutputAsync(); },
                OnStderr = async m => { stderr.Append(m.Text); await SaveOutputAsync(); },
                OnInit = async init => {
                    receipt = receipt with { ProviderCommandId = init.Id, State = SandboxExecutionState.Running, Phase = command.Background ? "launchPending" : "execution" };
                    receipt = await SaveReceiptAsync(receipt, false, ct);
                }
            };
            var opts = CommandOptions(lease, command.Cwd, timeout, command.Background);
            var execution = await GuardCommandStorageAsync(lease, token => command.Command is { } shell
                ? sdk.Commands.RunAsync(SandboxCommandPermissions.Shell(shell), opts, handlers, token)
                : sdk.Commands.RunAsync(SandboxCommandPermissions.Argv(command.Argv!), opts, handlers, token), ct);
            if (command.Background && (execution.Error is not null || execution.Complete is null || string.IsNullOrWhiteSpace(execution.Id)))
                throw new InvalidOperationException("Background launch acknowledgement was not confirmed.");
            if (!command.Background && (execution.ExitCode is null or < 0 or > 255
                || execution.Error?.Name?.Contains("Timeout",StringComparison.OrdinalIgnoreCase) == true))
                throw execution.Error?.Name?.Contains("Timeout", StringComparison.OrdinalIgnoreCase) == true
                    ? new TimeoutException("Command timeout; termination requires confirmation.")
                    : new InvalidOperationException("Command termination was not confirmed.");
            receipt = receipt with { ProviderCommandId = execution.Id ?? receipt.ProviderCommandId, Phase = "execution", Stdout = stdout.Text, Stderr = stderr.Text,
                Truncated = stdout.Truncated || stderr.Truncated, State = command.Background ? SandboxExecutionState.Running : SandboxExecutionState.Completed,
                ExitCode = execution.ExitCode, FinishedAt = command.Background ? null : time.GetUtcNow() };
            if (!command.Background) receipt = receipt with { DurationMs = Math.Max(0, execution.Complete?.ExecutionTimeMs ?? (long)(time.GetUtcNow() - (receipt.CommandStartedAt ?? receipt.StartedAt)).TotalMilliseconds) };
            if (!command.Background)
            {
                receipt = receipt with { Phase = "processInspection" };
                receipt = await SaveReceiptAsync(receipt, false, ct); // Completion survives a crash before retirement.
                FinalizationCheckpoint?.Invoke("commandCompleted");
                (lease, receipt) = await FinalizeCompletedExecutionAsync(sdk, lease, receipt, definition, fence, ct);
                return FinalizedResult(lease, receipt);
            }
            receipt = await SaveReceiptAsync(receipt, false, ct);
            return FinalizedResult(lease, receipt);
        }
        catch (Exception error) when (error is not FinalizationCrash)
        {
            if (error is AgentCoreException core) stderr.Append("\n" + core.Message);
            else stderr.Append("\nSandbox finalization could not be confirmed. Inspect the retained command and cleanup evidence; do not replay automatically.");
            try
            {
                receipt = await ReadReceiptAsync(lease, receipt.Id, CancellationToken.None) ?? receipt;
                if (!receipt.TerminationConfirmed)
                    receipt = await SaveReceiptAsync(receipt with { Stdout = stdout.Text, Stderr = stderr.Text, Truncated = stdout.Truncated || stderr.Truncated }, false, CancellationToken.None);
                (lease, receipt) = await AbortExecutionFinalizationAsync(lease, receipt, error);
            }
            catch
            {
                if (error is OperationCanceledException) throw;
                return await UnsettledFailureResultAsync(lease, receipt);
            }
            if (error is OperationCanceledException) throw;
            return FinalizedResult(lease, receipt);
        }
    }
}
