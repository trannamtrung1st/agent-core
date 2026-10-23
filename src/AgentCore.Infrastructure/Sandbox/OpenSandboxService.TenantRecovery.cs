using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private TenantSandboxGenerationAuthority TenantGeneration => tenantGenerationAuthority ?? throw new AccessDeniedException("tenant_effect_unavailable");
    private readonly SemaphoreSlim _tenantRecoveryGate = new(1, 1);
    private string? _tenantRecoveryCursor;
    private static readonly HttpClient RetirementClient = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };

    private async ValueTask RequireTenantRetirementCapabilityAsync(CancellationToken ct, bool verification = false)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.Origin(), "/agent-core/generations/capabilities"));
            request.Headers.Add("OPEN-SANDBOX-API-KEY", options.ApiKey);
            using var response = await RetirementClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new AccessDeniedException("tenant_sandbox_recovery_unavailable");
            using var document = await ReadRetirementResponseAsync(response, ct);
            if (document is null || document.RootElement.GetProperty("contractVersion").GetInt32() != 1 || !document.RootElement.GetProperty(verification ? "verificationFencing" : "retirementFencing").GetBoolean()
                || verification && (!document.RootElement.TryGetProperty("verificationScopeVersion", out var scopeVersion) || scopeVersion.GetInt32() != 2))
                throw new AccessDeniedException("tenant_sandbox_recovery_unavailable");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new AccessDeniedException("tenant_sandbox_recovery_unavailable"); }
    }

    private static async ValueTask<JsonDocument?> ReadRetirementResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream(); var buffer = new byte[1024]; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0) { if (bytes.Length + count > 4096) return null; bytes.Write(buffer, 0, count); }
        return JsonDocument.Parse(bytes.ToArray());
    }

    private async ValueTask<bool> TryRetireTenantGenerationAsync(SandboxLease lease, CancellationToken ct)
    {
        var scope = new { tenant = lease.TenantId!.Value.ToString("N"), session = lease.SessionId.ToString("N"),
            executor = lease.ExecutorPrincipalId!.Value.ToString("N"), generation = lease.Generation.ToString("N") };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Origin(), "/agent-core/generations/retire"));
            request.Headers.Add("OPEN-SANDBOX-API-KEY", options.ApiKey); request.Content = JsonContent.Create(scope);
            using var response = await RetirementClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return false;
            using var document = await ReadRetirementResponseAsync(response, ct);
            if (document is null) return false;
            var result = document.RootElement;
            return result.GetProperty("contractVersion").GetInt32() == 1 && result.GetProperty("retired").GetBoolean()
                && result.GetProperty("tenant").GetString() == scope.tenant && result.GetProperty("session").GetString() == scope.session
                && result.GetProperty("executor").GetString() == scope.executor && result.GetProperty("generation").GetString() == scope.generation;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; } // No body, provider path or secret becomes user evidence.
    }

    private async ValueTask ReconcileTenantGenerationsAsync(CancellationToken ct)
    {
        if (tenantRuntime is null || !await _tenantRecoveryGate.WaitAsync(0, ct)) return;
        try
        {
            var page = await TenantRuntime.ReadTenantSandboxRecoveryPageAsync(_tenantRecoveryCursor, 25, ct);
            foreach (var item in page.Items)
            {
                ct.ThrowIfCancellationRequested();
                var gate = Gate(item.SessionId);
                if (!await gate.WaitAsync(0, ct)) { _tenantRecoveryCursor = item.SessionId.ToString("D"); continue; }
                try
                {
                    var lease = item;
                    var executionId = lease.ActiveExecutionId ?? lease.LastExecutionId;
                    var facts = executionId is { } id ? await TenantRuntime.ReadTenantSandboxExecutionFactsAsync(lease.TenantId!.Value, lease.SessionId, id, ct) : null;
                    if (facts?.Lease is { } current)
                    {
                        if (current.Generation != item.Generation || facts.Execution.Generation != current.Generation
                            || (current.ActiveExecutionId ?? current.LastExecutionId) != facts.Execution.Id) continue;
                        lease = current;
                    }
                    var execution = facts?.Execution;
                    if (execution?.ApprovalRequestId is not null && approvedSandbox is not null && (!HasPendingFinalization(execution) || execution.TerminationConfirmed))
                        await approvedSandbox.SettleAsync(lease.TenantId!.Value, lease.SessionId, execution.Id, ct);
                    if (lease.State == SandboxLeaseState.Terminated) continue;
                    var stopPending = execution is { ProcessStop: not null, TerminationConfirmed: false };
                    if (execution is not null && (lease.State is SandboxLeaseState.Ready or SandboxLeaseState.Executing)
                        && (!stopPending || execution is { Background: true, State: SandboxExecutionState.Running, Phase: "execution", ProcessStop.DispatchStartedAt: null }))
                    {
                        try
                        {
                            await TenantGeneration.RequireAsync(lease, execution, ct);
                            await CheckMountedAuthorityAsync(lease, ct);
                            CanonicalSandboxMountResolver.CheckUsage(ResolveMounts(lease), lease.Profile.StorageBytes, ct);
                            if (lease.State == SandboxLeaseState.Ready)
                            { await using var connected = await ConnectAsync(lease, ct); continue; }
                            // Only a confirmed background launch is observed; stranded foreground/provisioning work is retired.
                            if (execution.Background)
                            {
                                if (stopPending)
                                {
                                    var claimed = await TenantRuntime.TryStartTenantSandboxProcessStopAsync(lease.TenantId!.Value, lease.SessionId, execution.Id,
                                        execution.ProcessStop!.RequestId, execution.Revision, lease.Revision, time.GetUtcNow(), ct);
                                    if (claimed is null) continue;
                                    execution = claimed;
                                }
                                await RefreshProcessLockedAsync(lease, execution, stopPending, ct); continue;
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch { lease = await ReadLeaseAsync(lease, ct) ?? lease; }
                    }
                    lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminating, CleanupReason = lease.CleanupReason ?? (stopPending ? "processStop" : "authorityOrLifetime"),
                        CleanupPasses = checked(lease.CleanupPasses + 1), LastCleanupAttemptAt = time.GetUtcNow() }, ct);
                    var confirmed = await TryTerminateRemoteAsync(lease, ct);
                    if (execution is not null)
                    {
                        execution = await ReadReceiptAsync(lease, execution.Id, ct) ?? execution;
                        if (!execution.TerminationConfirmed || confirmed && !execution.RemoteTerminationConfirmed)
                        {
                            var denied = !execution.FilesystemEffectsMayHaveOccurred && execution.State == SandboxExecutionState.Dispatching;
                            var reset = lease.CleanupReason == "reset" && execution.State is SandboxExecutionState.Dispatching or SandboxExecutionState.Running;
                            var stopped = execution.ProcessStop is not null && execution.State is SandboxExecutionState.Dispatching or SandboxExecutionState.Running;
                            execution = await SaveReceiptAsync(execution with {
                                State = execution.State is SandboxExecutionState.Dispatching or SandboxExecutionState.Running ? denied || reset || stopped ? SandboxExecutionState.Canceled : SandboxExecutionState.Indeterminate : execution.State,
                                Phase = denied ? "notDispatched" : execution.Phase, FinishedAt = execution.FinishedAt ?? time.GetUtcNow(),
                                CancellationReason = reset ? "humanReset" : stopped ? "humanStop" : execution.CancellationReason,
                                RemoteTerminationConfirmed = confirmed, TerminationConfirmed = execution.TerminationConfirmed || confirmed || denied }, false, ct);
                        }
                    }
                    await SaveAsync(lease with { State = confirmed ? SandboxLeaseState.Terminated : SandboxLeaseState.NeedsReconciliation,
                        ActiveExecutionId = confirmed ? null : lease.ActiveExecutionId,
                        TransitionPhase = lease.SelectionWitness is null ? lease.TransitionPhase : confirmed ? "awaitingReview" : "retirement",
                        SafeMessage = lease.ProviderHostScope != HostScope ? "Sandbox server or workspace mapping changed. Ask the host operator to restore the original mapping or confirm retirement on its original server. This generation cannot be adopted or replayed."
                            : confirmed ? "Owned generation retirement confirmed. Canonical files and durable outcomes are retained; no command or initialization was replayed."
                            : "Owned generation retirement is pending. Writes may still occur; the saved action cannot replay." }, ct);
                    if (execution?.ApprovalRequestId is not null && approvedSandbox is not null)
                        await approvedSandbox.SettleAsync(lease.TenantId!.Value, lease.SessionId, execution.Id, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* Preserve the exact durable fence for a later bounded pass. Never replay an effect. */ }
                finally { gate.Release(); _tenantRecoveryCursor = item.SessionId.ToString("D"); }
            }
            _tenantRecoveryCursor = page.NextCursor;
        }
        finally { _tenantRecoveryGate.Release(); }
    }
}
