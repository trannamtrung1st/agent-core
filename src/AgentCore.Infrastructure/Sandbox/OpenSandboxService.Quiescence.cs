using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.Logging;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    internal Func<CancellationToken, Task<bool>>? CompletedProcessInspection { get; set; }
    private async Task<bool> CompletedGenerationIsQuiescentAsync(SdkSandbox? sdk, SandboxLease lease, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return CompletedProcessInspection is { } inspect ? await inspect(ct) : sdk is not null && await IsQuiescentAsync(sdk, lease, ct);
        }
        catch (Exception error) when (error is not FinalizationCrash && !ct.IsCancellationRequested)
        {
            // Main-command completion is already durable. Unavailable inspection
            // forbids reuse, but authoritative owned-container absence can still
            // make retained staging safe to validate under the live commit fence.
            logger?.LogWarning("Sandbox process inspection unavailable; retiring the owned generation. SessionId={SessionId} Generation={Generation}",lease.SessionId,lease.Generation);
            return false;
        }
    }

    private async Task<bool> IsQuiescentAsync(SdkSandbox sdk, SandboxLease lease, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var current = await ProcessIdentitiesAsync(sdk, budget.Token);
                if (!current.Except(lease.BaselineProcesses ?? [], StringComparer.Ordinal).Any()) return true;
                if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(250), time, budget.Token);
            }
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
    // Setup must remain usable in the same generation; it cannot be replayed to
    // replace container-only state lost through retirement. No SDK-specific cleanup.
    private async Task ConfirmQuiescentAsync(SdkSandbox sdk, SandboxLease lease, CancellationToken ct)
    {
        if (!await IsQuiescentAsync(sdk, lease, ct))
            throw new AgentCoreException("SandboxProcessesActive", "Initialization left untracked processes. No scratch was committed; make setup quiescent or use a prebuilt image before an explicit reset.", 409);
    }
}
