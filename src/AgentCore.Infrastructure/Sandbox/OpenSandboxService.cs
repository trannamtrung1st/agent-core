using System.Collections.Concurrent;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using OpenSandbox;
using OpenSandbox.Config;
using OpenSandbox.Core;
using OpenSandbox.Models;
using Microsoft.Extensions.Logging;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

/// <summary>One SDK adapter; authoritative files and durable ownership remain in Core.</summary>
public sealed partial class OpenSandboxService(OpenSandboxOptions options, ISandboxStore store,
    ITenantSandboxWorkspacePreparer workspace, IMemoryStore sessions, TimeProvider time,
    ILogger<OpenSandboxService>? logger = null, SandboxImageAuthority? imageAuthority = null, ITenantSandboxVerificationStore? tenantVerifications = null,
    AgentCore.Application.Security.TenantSandboxImageAuthorization? verificationAuthority = null,
    ITenantSandboxRuntimeStore? tenantRuntime = null, AgentCore.Application.Security.TenantSandboxApprovedOperationService? approvedSandbox = null,
    AgentCore.Application.Security.TenantSandboxExecutionAuthority? tenantExecutionAuthority = null, ITenantRunStore? tenantRuns = null, AgentCore.Application.Security.TenantSandboxGenerationAuthority? tenantGenerationAuthority = null) : ISandboxService, ITenantSandboxVerifier, ITenantSandboxCommandExecutor
{
    private SandboxImageAuthority Images => imageAuthority ?? new(options, store, time);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private SemaphoreSlim Gate(Guid id) => _gates.GetOrAdd(id, _ => new(1, 1));
    internal Action<SandboxExecution>? ExecutionReceiptFault { get; set; }
    internal Action<string>? FinalizationCheckpoint { get; set; }
    internal sealed class FinalizationCrash : Exception { }
    private async ValueTask<SandboxExecution> SaveReceiptAsync(SandboxExecution execution, bool create, CancellationToken ct)
    {
        ExecutionReceiptFault?.Invoke(execution);
        if (execution.TenantId is { } tenant)
        {
            if (create) throw new AgentCore.Application.Security.AccessDeniedException("sandbox_approval_context_required");
            var next = execution with { Revision = checked(execution.Revision + 1) };
            if (!await TenantRuntime.TrySaveTenantSandboxExecutionFactsAsync(tenant, next, execution.Revision, ct))
                throw new AgentCore.Application.Security.SecurityConflictException("stale_revision");
            return next;
        }
        await store.SaveExecutionAsync(execution, create, ct); return execution;
    }
    private ConnectionConfig Connection()
    {
        var uri = options.Origin();
        return new(new ConnectionConfigOptions { Domain = uri.Authority, Protocol = uri.Scheme == "https" ? ConnectionProtocol.Https : ConnectionProtocol.Http,
            ApiKey = options.ApiKey, UseServerProxy = true, RequestTimeoutSeconds = 210, DisableMetrics = true });
    }
    private const int ProcessCleanupGraceSeconds = 20;
    private async ValueTask<SandboxLease> ReserveExecutionLifetimeAsync(SdkSandbox sdk, SandboxLease lease, int commandSeconds, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var lifetimeEnd = lease.CreatedAt.AddSeconds(lease.Profile.MaxLifetimeSeconds);
        var minimumEnd = now.AddSeconds(commandSeconds + ProcessCleanupGraceSeconds);
        if (minimumEnd > lifetimeEnd)
            throw AgentCoreErrors.Conflict("Sandbox maximum lifetime cannot cover this command and process cleanup. Reset before starting it.");
        var target = now.AddSeconds(Math.Max(lease.Profile.IdleTtlSeconds, commandSeconds + ProcessCleanupGraceSeconds));
        if (target > lifetimeEnd) target = lifetimeEnd;
        var seconds = Math.Max(1, (int)(target - now).TotalSeconds);
        await sdk.RenewAsync(seconds, ct);
        target = now.AddSeconds(seconds);
        return await SaveAsync(lease with { ExpiresAt = target }, ct);
    }
    private async ValueTask<DateTimeOffset> RenewIdleAsync(SdkSandbox sdk, SandboxLease lease, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var target = now.AddSeconds(lease.Profile.IdleTtlSeconds);
        var hardEnd = lease.CreatedAt.AddSeconds(lease.Profile.MaxLifetimeSeconds);
        if (target > hardEnd) target = hardEnd;
        if (target <= now) return hardEnd;
        await sdk.RenewAsync(Math.Max(1, (int)(target - now).TotalSeconds), ct);
        return target;
    }
    private static bool MatchesGeneration(SandboxLease lease, IReadOnlyDictionary<string, string>? metadata) =>
        metadata?.GetValueOrDefault("agent-core-generation") == lease.Generation.ToString("N")
        && (lease.TenantId is null ? string.IsNullOrEmpty(metadata.GetValueOrDefault("agent-core-tenant"))
            && string.IsNullOrEmpty(metadata.GetValueOrDefault("agent-core-session")) && string.IsNullOrEmpty(metadata.GetValueOrDefault("agent-core-executor"))
            : metadata.GetValueOrDefault("agent-core-tenant") == lease.TenantId.Value.ToString("N")
            && metadata.GetValueOrDefault("agent-core-session") == lease.SessionId.ToString("N")
            && metadata.GetValueOrDefault("agent-core-executor") == lease.ExecutorPrincipalId?.ToString("N"));
    private void RequireTenantProviderScope(SandboxLease lease)
    {
        if (lease.TenantId is not null && lease.ProviderHostScope != HostScope)
            throw new AgentCore.Application.Security.AccessDeniedException("sandbox_host_scope_changed");
    }
    private async Task<SdkSandbox> ConnectAsync(SandboxLease lease, CancellationToken ct)
    {
        RequireTenantProviderScope(lease);
        if (lease.MountAuthority is null) throw CanonicalSandboxMountResolver.Incompatible("Environment mount authority is absent; reset and reverify the profile.");
        if (lease.ProviderId is null) throw AgentCoreErrors.Conflict("Sandbox provisioning requires reconciliation.");
        var sdk = await SdkSandbox.ConnectAsync(new SandboxConnectOptions { SandboxId = lease.ProviderId, ConnectionConfig = Connection() }, ct);
        try
        {
            var info = await sdk.GetInfoAsync(ct);
            if (!MatchesGeneration(lease, info.Metadata))
                throw AgentCoreErrors.NotFound("Sandbox generation was not found.");
            return sdk;
        }
        catch { await sdk.DisposeAsync(); throw; }
    }
    private async ValueTask<SandboxLease> SaveAsync(SandboxLease lease, CancellationToken ct)
    {
        var next = lease with { Revision = checked(lease.Revision + 1) };
        if (lease.TenantId is { } tenant)
        {
            if (!await TenantRuntime.TrySaveTenantSandboxLeaseFactsAsync(tenant, next, lease.Revision, ct))
                throw new AgentCore.Application.Security.SecurityConflictException("stale_revision");
        }
        else await store.SaveLeaseAsync(next, lease.Revision, ct);
        return next;
    }
    private async Task<bool> TryTerminateRemoteAsync(SandboxLease lease, CancellationToken ct)
    {
        if (lease.TenantId is not null && lease.ProviderHostScope != HostScope) return false;
        if (lease.ProviderId is null) return lease.TenantId is not null && await TryRetireTenantGenerationAsync(lease, ct);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromSeconds(20));
        string failure = "transport"; int? status = null;
        // DELETE is idempotent for this exact durable provider identity. A lost
        // acknowledgement may already have removed it; 404 confirms cleanup.
        // Shell dispatch remains non-replayable. Keep the existing total budget.
        for (var attempt = 0; attempt < 3 && !budget.IsCancellationRequested; attempt++)
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            request.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await using var manager = SandboxManager.Create(new SandboxManagerOptions { ConnectionConfig = Connection() });
                try
                {
                    var info = await manager.GetSandboxInfoAsync(lease.ProviderId, request.Token);
                    if (!MatchesGeneration(lease, info.Metadata)) { failure = "ownership"; break; }
                }
                catch (SandboxApiException e) when (e.StatusCode == 404) { return true; }
                try { await manager.KillSandboxAsync(lease.ProviderId, request.Token); }
                catch (SandboxApiException e) when (e.StatusCode == 404) { /* Confirmed absent; local cleanup is still required. */ }
                return true;
            }
            catch (SandboxApiException e)
            {
                failure = "http"; status = e.StatusCode;
                if (e.StatusCode is not (408 or 429 or 500 or 502 or 503 or 504)) break;
            }
            catch (OperationCanceledException) { failure = "timeout"; status = null; if (budget.IsCancellationRequested) break; }
            catch (HttpRequestException) { failure = "transport"; status = null; }
            catch { failure = "transport"; status = null; break; }
            if (attempt < 2)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(250), budget.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        // Never log the SDK exception, body, route, provider ID or credentials.
        logger?.LogWarning("Sandbox cleanup remains pending. SessionId={SessionId} Generation={Generation} Failure={Failure} StatusCode={StatusCode}",
            lease.SessionId, lease.Generation, failure, status);
        return false;
    }
    public ValueTask StopSessionAsync(Guid trustedSessionId, CancellationToken ct = default, Guid? expectedGeneration = null, SandboxStopIntent intent = SandboxStopIntent.SessionLifecycle)
        => StopSessionAsync(trustedSessionId, ct, expectedGeneration, null, false, intent);
    public async ValueTask StopSessionAsync(Guid trustedSessionId, CancellationToken ct, Guid? expectedGeneration, Guid? expectedExecutionId, bool checkExecutionIdentity, SandboxStopIntent intent = SandboxStopIntent.SessionLifecycle)
    {
        var gate = Gate(trustedSessionId); await gate.WaitAsync(ct);
        try
        {
            var lease = await store.GetLeaseAsync(trustedSessionId, ct);
            if (expectedGeneration is not null && lease?.Generation != expectedGeneration) throw AgentCoreErrors.Conflict("Sandbox generation changed. Refresh before resetting.");
            if (lease is null) return;
            if (checkExecutionIdentity && (lease.ActiveExecutionId ?? lease.LastExecutionId) != expectedExecutionId)
                throw AgentCoreErrors.Conflict("Sandbox execution changed. Inspect the latest evidence before resetting.");
            if (lease.State == SandboxLeaseState.Terminated)
            {
                if (intent == SandboxStopIntent.OwnerReset && lease.CleanupReason != "reset")
                    await SaveAsync(lease with { CleanupReason = "reset", SelectedProfileId = null, TransitionProfileId = null, TransitionProfileRevision = null, TransitionPhase = null }, ct);
                return;
            }
            var metadata = await sessions.LoadMetadataAsync(trustedSessionId, ct);
            var reason = intent == SandboxStopIntent.OwnerReset ? "reset" : intent == SandboxStopIntent.PolicyChanged ? "policyChanged" : metadata is null || metadata.DurablyDeletedAt is not null ? "sessionDeleted"
                : metadata.ArchivedAt is not null ? "sessionArchived" : metadata.Status is SessionStatus.Ending or SessionStatus.Ended ? "sessionEnded"
                : metadata.LifecycleStatus != SessionLifecycleStatus.Active ? "sessionDeactivated" : "sessionLifecycle";
            lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminating, CleanupReason = reason, SelectedProfileId = reason == "reset" ? null : lease.SelectedProfileId,
                TransitionProfileId = reason == "reset" ? null : lease.TransitionProfileId, TransitionProfileRevision = reason == "reset" ? null : lease.TransitionProfileRevision, TransitionPhase = reason == "reset" ? null : lease.TransitionPhase, CleanupPasses = lease.CleanupPasses + 1, LastCleanupAttemptAt = time.GetUtcNow() }, ct);
            var pending = lease.ActiveExecutionId is { } id ? await store.GetExecutionAsync(trustedSessionId, id, ct) : null;
            if (pending is null && lease.LastExecutionId is { } lastId
                && await store.GetExecutionAsync(trustedSessionId, lastId, ct) is { } last
                && (HasPendingFinalization(last)))
                pending = last; // An interrupted lease update may have cleared the active pointer.
            if (pending is not null)
            {
                // Persist termination intent before touching the remote generation.
                pending = pending with { FailureCategory = pending.FailureCategory ?? "cancellation",
                    CancellationReason = reason == "reset" ? "ownerReset" : reason == "policyChanged" ? "policyChanged" : "sessionLifecycle" };
                await SaveReceiptAsync(pending, false, ct);
            }
            if (!await TryTerminateRemoteAsync(lease, ct)) throw AgentCoreErrors.Persistence("Sandbox cleanup is pending; its durable receipt will be reconciled.");
            if (pending is not null)
            {
                if (pending.State is SandboxExecutionState.Dispatching or SandboxExecutionState.Running)
                    pending = pending with { State = SandboxExecutionState.Canceled, FinishedAt = time.GetUtcNow() };
                await SettleTerminatedExecutionAsync(lease, pending, ct);
                if ((await store.GetLeaseAsync(trustedSessionId, ct))?.State != SandboxLeaseState.Terminated)
                    throw AgentCoreErrors.Persistence("Sandbox process termination remains unconfirmed; retained evidence must be reconciled.");
            }
            else
            {
                await SaveAsync(lease with { State = SandboxLeaseState.Terminated, ActiveExecutionId = null }, ct);
            }
        }
        finally { gate.Release(); }
    }
    public async ValueTask ReconcileAsync(CancellationToken ct = default)
    {
        using (var tenantPass = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            tenantPass.CancelAfter(TimeSpan.FromSeconds(15));
            try { await ReconcileTenantGenerationsAsync(tenantPass.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* Continue existing cleanup with its remaining pass budget. */ }
        }
        foreach (var item in await store.ListLeasesAsync(ct))
        {
            var gate = Gate(item.SessionId); if (!await gate.WaitAsync(0, ct)) continue;
            try
            {
                var lease = await store.GetLeaseAsync(item.SessionId, ct);
                if (lease is null || lease.State == SandboxLeaseState.Terminated) continue;
                // Unowned generations retain cleanup evidence only. Never refresh,
                // reconnect, inspect command logs or infer execution authority.
                lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminating, CleanupReason = lease.CleanupReason ?? "legacyAuthorityRetired",
                    CleanupPasses = lease.CleanupPasses + 1, LastCleanupAttemptAt = time.GetUtcNow() }, ct);
                if (lease.ProviderId is null)
                {
                    await using var manager = SandboxManager.Create(new SandboxManagerOptions { ConnectionConfig = Connection() });
                    var page = await manager.ListSandboxInfosAsync(new SandboxFilter { Metadata = new Dictionary<string,string> { ["agent-core-generation"] = lease.Generation.ToString("N") }, PageSize = 100 }, ct);
                    var exact = true;
                    foreach (var remote in page.Items)
                    {
                        if (!MatchesGeneration(lease, remote.Metadata)) { exact = false; continue; }
                        await manager.KillSandboxAsync(remote.Id, ct);
                    }
                    if (exact && page.Items.Count < 100) await FinalizeCleanupAsync(lease, ct);
                }
                else if (await TryTerminateRemoteAsync(lease, ct)) await FinalizeCleanupAsync(lease, ct);
            }
            catch { /* Keep the durable cleanup fence for the next bounded pass. */ }
            finally { gate.Release(); }
        }
    }
    private async ValueTask FinalizeCleanupAsync(SandboxLease lease, CancellationToken ct)
    {
        var id = lease.ActiveExecutionId ?? lease.LastExecutionId;
        if (id is { } executionId && await store.GetExecutionAsync(lease.SessionId, executionId, ct) is { } execution)
        {
            if (execution.State is SandboxExecutionState.Dispatching or SandboxExecutionState.Running && lease.CleanupReason is "reset" or "cancelled" or "sessionLifecycle" or "sessionDeleted" or "sessionArchived" or "sessionEnded" or "policyChanged")
                execution = execution with { State = SandboxExecutionState.Canceled, FailureCategory = "cancellation", CancellationReason = lease.CleanupReason == "reset" ? "ownerReset" : lease.CleanupReason == "policyChanged" ? "policyChanged" : "sessionLifecycle" };
            await SettleTerminatedExecutionAsync(lease, execution, ct);
            return;
        }
        await SaveAsync(lease with { State = SandboxLeaseState.Terminated, ActiveExecutionId = null }, ct);
    }
    private static SandboxCommandResult Result(SandboxExecution e) => new(e.Id, e.State.ToString(), e.ExitCode, e.Stdout, e.Stderr, e.Truncated,
        e.DurationMs ?? (e.FinishedAt is { } end ? Math.Max(0, (long)(end - e.StartedAt).TotalMilliseconds) : 0), e.FilesystemEffectsMayHaveOccurred, e.DiagnosticId, e.Background, e.CommandText, e.ProfileId, e.ProfileRevision, e.Generation, e.Phase, e.FailureCategory, e.CancellationReason, e.TerminationConfirmed, GenerationRecycled: e.GenerationRecycled, RemoteTerminationConfirmed: e.RemoteTerminationConfirmed);
    private sealed class BoundedText(int maxBytes)
    {
        private readonly StringBuilder _text = new(); private int _bytes;
        public bool Truncated { get; private set; } public string Text => _text.ToString();
        public void Append(string value) { foreach (var rune in value.EnumerateRunes()) { if (_bytes + rune.Utf8SequenceLength > maxBytes) { Truncated = true; return; } _text.Append(rune.ToString()); _bytes += rune.Utf8SequenceLength; } }
    }
}
