using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    public async ValueTask<SandboxCommandResult> InspectProcessAsync(Guid trustedSessionId, Guid processId, CancellationToken ct = default)
    {
        var execution = await store.GetExecutionAsync(trustedSessionId, processId, ct) ?? throw AgentCoreErrors.NotFound("Owned execution was not found.");
        // GET/status observes durable receipts. Only reconciliation or explicit
        // stop may finalize a process; reading never imports files or interrupts it.
        if (execution.WorkspaceSyncStatus == "committing")
            execution = execution with { WorkspaceSyncStatus = await CommitStatusAsync(execution, ct) };
        return Result(execution);
    }
    public ValueTask<SandboxCommandResult> StopProcessAsync(Guid trustedSessionId, Guid processId, CancellationToken ct = default) => ProcessAsync(trustedSessionId, processId, true, ct);
    private async ValueTask<SandboxCommandResult> ProcessAsync(Guid sessionId, Guid processId, bool stop, CancellationToken ct)
    {
        var observed = await store.GetExecutionAsync(sessionId, processId, ct) ?? throw AgentCoreErrors.NotFound("Owned execution was not found.");
        if (!stop && (!observed.Background || observed.State is SandboxExecutionState.Completed or SandboxExecutionState.Canceled or SandboxExecutionState.Indeterminate))
            return Result(observed);
        var gate = Gate(sessionId); await gate.WaitAsync(ct);
        try
        {
            var execution = await store.GetExecutionAsync(sessionId, processId, ct) ?? throw AgentCoreErrors.NotFound("Owned process was not found.");
            if (execution.State is SandboxExecutionState.Completed or SandboxExecutionState.Canceled or SandboxExecutionState.Indeterminate) return Result(execution);
            var lease = await store.GetLeaseAsync(sessionId, ct);
            if (lease is null || lease.Generation != execution.Generation || lease.ActiveExecutionId != execution.Id) throw AgentCoreErrors.NotFound("Active process generation was not found.");
            return await RefreshProcessLockedAsync(lease, execution, stop, ct);
        }
        finally { gate.Release(); }
    }
    private async ValueTask<SandboxCommandResult> RefreshProcessLockedAsync(SandboxLease lease, SandboxExecution execution, bool stop, CancellationToken ct)
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
        if (status.Running == false)
        {
            var session = await sessions.LoadMetadataAsync(lease.SessionId, ct);
            if (session is null || session.DurablyDeletedAt is not null || session.ArchivedAt is not null || session.LifecycleStatus != SessionLifecycleStatus.Active)
                throw AgentCoreErrors.Conflict("Process owner is no longer active; scratch cannot be committed.");
            var scratchCommitted = false;
            try
            {
                await ValidateProcessCommitAsync(lease, execution, ct);
                await ConfirmQuiescentAsync(sdk, lease, ct);
                var transfer = await CollectAsync(sdk, lease.Profile, ct);
                execution = execution with { WorkspaceSyncStatus = "committing" };
                await SaveReceiptAsync(execution, false, ct);
                await workspace.CommitScratchAsync(lease.SessionId, transfer, lease.ScratchFingerprint!, ct,
                    token => ValidateProcessCommitAsync(lease, execution, token), execution.Id);
                scratchCommitted = true;
                execution = execution with { State = stop ? SandboxExecutionState.Canceled : SandboxExecutionState.Completed, ExitCode = status.ExitCode,
                    FinishedAt = time.GetUtcNow(), WorkspaceSyncStatus = "committed" };
                await SaveReceiptAsync(execution, false, CancellationToken.None);
                await SaveAsync(lease with { State = SandboxLeaseState.Ready, ActiveExecutionId = null, LastActivityAt = time.GetUtcNow(), ExpiresAt = await RenewIdleAsync(sdk, lease, ct),
                    ScratchFingerprint = (await workspace.CaptureScratchAsync(lease.SessionId, session.Definition, ct)).Fingerprint }, ct);
            }
            catch (Exception error)
            {
                execution = execution with { State = SandboxExecutionState.Indeterminate, FinishedAt=time.GetUtcNow(), WorkspaceSyncStatus = scratchCommitted ? "committed" : await CommitStatusAsync(execution, CancellationToken.None), DiagnosticId = Guid.CreateVersion7().ToString("D") };
                try
                {
                    await SaveAsync(lease with { State = SandboxLeaseState.NeedsResync, DiagnosticId = execution.DiagnosticId }, CancellationToken.None);
                    await SaveReceiptAsync(execution, false, CancellationToken.None);
                }
                catch { /* Preserve uncertainty even if its follow-up receipt cannot be saved. */ }
                if (error is OperationCanceledException) throw;
                return Result(execution);
            }
        }
        await SaveReceiptAsync(execution, false, ct); return Result(execution);
    }
    private async ValueTask ValidateProcessCommitAsync(SandboxLease lease, SandboxExecution execution, CancellationToken ct)
    {
        var owner = await sessions.LoadMetadataAsync(lease.SessionId, ct);
        var run = owner?.ProfileId is { } profile ? await runs.GetAsync(new(lease.AgentInstanceId, profile), execution.AgentRunId, ct) : null;
        if (lease.ExpiresAt <= time.GetUtcNow() || (execution.DeadlineAt ?? execution.StartedAt.AddSeconds(lease.Profile.MaxCommandSeconds)) <= time.GetUtcNow())
            throw AgentCoreErrors.Conflict("Process deadline expired; scratch cannot be committed.");
        if (run is null || run.CancellationRequested || run.Status is AgentRunStatus.Cancelled or AgentRunStatus.Failed)
            throw AgentCoreErrors.Conflict("Process Run was canceled or revoked; scratch cannot be committed.");
        if (instances is not null && (await instances.FindAsync(lease.AgentInstanceId,ct))?.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active)
            throw AgentCoreErrors.Conflict("Process Instance is no longer active; scratch cannot be committed.");
        if ((await Images.InspectAsync(lease.Profile, ct, requireVerification:false)).Status != "SourceAuthorized" || !options.Enabled || owner?.AgentInstanceId != lease.AgentInstanceId || owner.DurablyDeletedAt is not null || owner.ArchivedAt is not null
            || owner.LifecycleStatus != SessionLifecycleStatus.Active || owner.Status is SessionStatus.Ended or SessionStatus.Ending
            || (await store.GetProfileAsync(lease.Profile.Id, ct: ct))?.Enabled != true)
            throw AgentCoreErrors.Conflict("Process authority was revoked; scratch cannot be committed.");
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
