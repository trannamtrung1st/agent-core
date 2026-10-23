using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private async ValueTask<SandboxCommandResult> RefreshProcessLockedAsync(SandboxLease lease, SandboxExecution execution, bool stop, CancellationToken ct)
    {
        try
        {
            await CheckMountedAuthorityAsync(lease, ct);
            CanonicalSandboxMountResolver.CheckUsage(ResolveMounts(lease), lease.Profile.StorageBytes, ct);
            return await RefreshProcessCoreLockedAsync(lease, execution, stop, ct);
        }
        catch (Exception error) when (error is not FinalizationCrash)
        {
            // The commit path may already have persisted a terminal receipt before
            // propagating cancellation. Never replace that evidence with this
            // wrapper's older dispatch snapshot or repeat confirmed cleanup.
            var retained = await ReadReceiptAsync(lease, execution.Id, CancellationToken.None);
            if (retained is { TerminationConfirmed: true, State: SandboxExecutionState.Completed or SandboxExecutionState.Canceled or SandboxExecutionState.Indeterminate })
            {
                if (error is OperationCanceledException) throw;
                return Result(retained);
            }
            execution = retained ?? execution;
            try { (lease, execution) = await AbortExecutionFinalizationAsync(lease, execution, error); }
            catch
            {
                if (error is OperationCanceledException) throw;
                return await UnsettledFailureResultAsync(lease, execution);
            }
            if (error is OperationCanceledException) throw;
            return Result(execution);
        }
    }
    private async ValueTask<SandboxCommandResult> RefreshProcessCoreLockedAsync(SandboxLease lease, SandboxExecution execution, bool stop, CancellationToken ct)
    {
        if (execution.ProviderCommandId is null) throw AgentCoreErrors.Conflict("Process dispatch is uncertain. Reset to recover; do not repeat the command.");
        await using var sdk = await ConnectAsync(lease, ct);
        if (stop) await sdk.Commands.InterruptAsync(execution.ProviderCommandId, ct);
        var status = await sdk.Commands.GetCommandStatusAsync(execution.ProviderCommandId, ct);
        for (var attempt = 0; stop && status.Running == true && attempt < 20; attempt++)
        { await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct); status = await sdk.Commands.GetCommandStatusAsync(execution.ProviderCommandId, ct); }
        if (status.Running is null || stop && status.Running == true) throw AgentCoreErrors.Conflict("Process termination is unconfirmed. Inspect or reset before continuing.");
        if (execution.Background)
        {
            var logs = await BoundedBackgroundLogsAsync(lease, execution.ProviderCommandId, ct);
            execution = execution with { Stdout = logs.Text, Truncated = logs.Truncated };
        }
        // Discard captured status/logs if authority changed during provider I/O, including a still-running process.
        await ValidateProcessAuthorityAsync(lease, execution, ct);
        if (status.Running == false)
        {
            var definition = (await TenantGeneration.RequireAsync(lease, execution, ct)).Admission.Configuration!.Definition;
            // The pinned execd reports Go's -1 when an explicitly interrupted
            // process was signal-terminated. Running=false is confirmed termination;
            // preserve that provider evidence without inventing a normal exit code.
            if (status.ExitCode is null or < 0 or > 255 && !(stop && status.ExitCode == -1))
                throw AgentCoreErrors.Conflict("Background command completion outcome is unconfirmed; inspect canonical files; effects may have occurred.");
            execution = execution with { State = stop ? SandboxExecutionState.Canceled : SandboxExecutionState.Completed,
                ExitCode = status.ExitCode, FinishedAt = time.GetUtcNow(), Phase = "processInspection",
                FailureCategory = stop ? "cancellation" : null, CancellationReason = stop ? execution.ProcessStop is null ? "processStop" : "humanStop" : null };
            execution = await SaveReceiptAsync(execution, false, ct);
            FinalizationCheckpoint?.Invoke("commandCompleted");
            (lease, execution) = await FinalizeCompletedExecutionAsync(sdk, lease, execution, definition,
                token => ValidateProcessAuthorityAsync(lease, execution, token), ct);
            return FinalizedResult(lease, execution);
        }
        execution = await SaveReceiptAsync(execution, false, ct); return Result(execution);
    }
    private async ValueTask ValidateProcessAuthorityAsync(SandboxLease lease, SandboxExecution execution, CancellationToken ct)
    {
        if (lease.TenantId is null) throw new AgentCore.Application.Security.AccessDeniedException("tenant_scope_required");
        RequireTenantProviderScope(lease); await TenantGeneration.RequireAsync(lease, execution, ct);
    }
    // SDK 1.1.1 accumulates the entire background log HTTP body. This narrow shim
    // uses the same authenticated server proxy and a bounded stream instead.
    private async Task<(string Text, bool Truncated)> BoundedBackgroundLogsAsync(SandboxLease lease, string commandId, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.Origin(), "/v1/sandboxes/" + Uri.EscapeDataString(lease.ProviderId!) + "/proxy/44772/command/" + Uri.EscapeDataString(commandId) + "/logs?cursor=0"));
        request.Headers.Add("OPEN-SANDBOX-API-KEY", options.ApiKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct); if (read == 0) break;
            if (bytes.Length + read > 65536) return ("Background output exceeded the bounded log window.", true);
            bytes.Write(buffer, 0, read);
        }
        var text = new BoundedText(32768); text.Append(System.Text.Encoding.UTF8.GetString(bytes.ToArray())); return (text.Text, text.Truncated);
    }
}
