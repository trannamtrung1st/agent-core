using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using OpenSandbox;
using OpenSandbox.Config;
using OpenSandbox.Core;
using OpenSandbox.Models;
using Microsoft.Extensions.Logging;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

/// <summary>One SDK adapter; authoritative files and durable ownership remain in Core.</summary>
public sealed partial class OpenSandboxService(OpenSandboxOptions options, ISandboxStore store,
    ISessionWorkspace workspace, IMemoryStore sessions, IAgentRunStore runs, TimeProvider time, IAgentInstanceStore? instances = null,
    ILogger<OpenSandboxService>? logger = null, SandboxImageAuthority? imageAuthority = null) : ISandboxService
{
    private SandboxImageAuthority Images => imageAuthority ?? new(options, store, time);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private SemaphoreSlim Gate(Guid id) => _gates.GetOrAdd(id, _ => new(1, 1));
    internal Action<SandboxExecution>? ExecutionReceiptFault { get; set; }
    private ValueTask SaveReceiptAsync(SandboxExecution execution, bool create, CancellationToken ct)
    {
        ExecutionReceiptFault?.Invoke(execution);
        return store.SaveExecutionAsync(execution, create, ct);
    }
    private ConnectionConfig Connection()
    {
        var uri = options.Origin();
        return new(new ConnectionConfigOptions { Domain = uri.Authority, Protocol = uri.Scheme == "https" ? ConnectionProtocol.Https : ConnectionProtocol.Http,
            ApiKey = options.ApiKey, UseServerProxy = true, RequestTimeoutSeconds = 210, DisableMetrics = true });
    }
    private async ValueTask ValidateAccessAsync(SandboxAccess access, CancellationToken ct)
    {
        var session = await sessions.LoadMetadataAsync(access.SessionId, ct);
        if (session is null || session.AgentInstanceId != access.AgentInstanceId || session.ProfileId is null || session.DurablyDeletedAt is not null
            || session.ArchivedAt is not null || session.LifecycleStatus != SessionLifecycleStatus.Active || session.Status is SessionStatus.Ended or SessionStatus.Ending)
            throw AgentCoreErrors.NotFound("Active sandbox Session was not found.");
        var run = await runs.GetAsync(new(access.AgentInstanceId, session.ProfileId.Value), access.AgentRunId, ct);
        if (instances is not null && (await instances.FindAsync(access.AgentInstanceId,ct))?.Lifecycle != AgentInstanceLifecycle.Active)
            throw AgentCoreErrors.NotFound("Active sandbox Instance was not found.");
        if (run?.SessionId != access.SessionId || run.Claim?.Generation != access.ClaimGeneration || run.CancellationRequested
            || run.Status != AgentRunStatus.Running || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow())
            throw AgentCoreErrors.Conflict("Sandbox command no longer owns the active Run claim.");
        if (!options.Enabled || (await store.GetProfileAsync(access.Profile.Id, ct: ct))?.Enabled != true)
            throw AgentCoreErrors.Forbidden("Sandbox profile is disabled or unavailable.");
        options.ValidateProfile(access.Profile);
        var source = await Images.InspectAsync(access.Profile, ct, requireVerification: false);
        if (source.Status != "SourceAuthorized") throw AgentCoreErrors.Forbidden(source.Message);
        var allowed = access.Definition.SandboxPolicy?.AllowedProfileIds;
        if (allowed is null || !allowed.Contains(access.Profile.Id, StringComparer.Ordinal))
            throw AgentCoreErrors.Forbidden("Definition does not allow this sandbox profile.");
    }

    public async ValueTask<SandboxCommandResult> ExecuteAsync(SandboxAccess access, SandboxCommandRequest command, CancellationToken ct = default)
    {
        SandboxCommands.Validate(command); await ValidateAccessAsync(access, ct);
        var gate = Gate(access.SessionId); await gate.WaitAsync(ct);
        try
        {
            await ValidateAccessAsync(access, ct);
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command))).ToLowerInvariant();
            var previous = await store.GetOperationAsync(access.SessionId, access.AgentRunId, access.ToolCallId, ct);
            if (previous is not null)
            {
                if (previous.ActionHash != hash) throw AgentCoreErrors.Conflict("Tool operation already names another command.");
                return Result(previous); // A receipt is observation, never permission to repeat.
            }
            var lease = await EnsureLockedAsync(access, ct);
            if (lease.State != SandboxLeaseState.Ready || lease.ActiveExecutionId is not null)
                throw AgentCoreErrors.Conflict("Sandbox requires process inspection, stop or reset before another command.");
            await using var sdk = await ConnectAsync(lease, ct);
            var timeout = Math.Min(command.TimeoutSeconds, lease.Profile.MaxCommandSeconds);
            var scratch = await workspace.CaptureScratchAsync(access.SessionId, access.Definition, ct);
            if (scratch.Fingerprint != lease.ScratchFingerprint) await HydrateAsync(sdk, scratch.Transfer, ct);
            lease = await ReserveExecutionLifetimeAsync(sdk, lease, timeout, ct);
            var receipt = new SandboxExecution(Guid.CreateVersion7(), access.SessionId, access.AgentInstanceId, lease.Generation,
                access.AgentRunId, access.ClaimGeneration, access.ToolCallId, hash, SandboxExecutionState.Dispatching, time.GetUtcNow(), Background: command.Background,
                DeadlineAt: time.GetUtcNow().AddSeconds(timeout), WorkspaceSyncStatus: "notCommitted",
                CommandText: command.Command ?? JsonSerializer.Serialize(command.Argv, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            await SaveReceiptAsync(receipt, true, ct);
            lease = await SaveAsync(lease with { State = SandboxLeaseState.Executing, ActiveExecutionId = receipt.Id, LastExecutionId = receipt.Id, LastActivityAt = time.GetUtcNow(), ScratchFingerprint = scratch.Fingerprint }, ct);
            var stdout = new BoundedText(32768); var stderr = new BoundedText(32768);
            var lastOutputSave = time.GetUtcNow().AddSeconds(-1);
            async Task SaveOutputAsync()
            {
                if (time.GetUtcNow() - lastOutputSave < TimeSpan.FromSeconds(1)) return;
                lastOutputSave = time.GetUtcNow();
                receipt = receipt with { Stdout = stdout.Text, Stderr = stderr.Text, Truncated = stdout.Truncated || stderr.Truncated };
                await SaveReceiptAsync(receipt, false, ct);
            }
            var scratchCommitted = false;
            try
            {
                var handlers = new ExecutionHandlers {
                    SkipAccumulation = true,
                    OnStdout = async m => { stdout.Append(m.Text); await SaveOutputAsync(); },
                    OnStderr = async m => { stderr.Append(m.Text); await SaveOutputAsync(); },
                    OnInit = async init => {
                        receipt = receipt with { ProviderCommandId = init.Id, State = SandboxExecutionState.Running };
                        await SaveReceiptAsync(receipt, false, ct);
                    }
                };
                var opts = new RunCommandOptions { WorkingDirectory = command.Cwd, Background = command.Background, TimeoutSeconds = timeout };
                var execution = command.Command is { } shell
                    ? await sdk.Commands.RunAsync(shell, opts, handlers, ct)
                    : await sdk.Commands.RunAsync(command.Argv!, opts, handlers, ct);
                if (!command.Background && (execution.ExitCode is null or < 0 or > 255
                    || execution.Error?.Name?.Contains("Timeout",StringComparison.OrdinalIgnoreCase) == true))
                    throw new InvalidOperationException("Command termination was not confirmed.");
                receipt = receipt with { ProviderCommandId = execution.Id ?? receipt.ProviderCommandId, Stdout = stdout.Text, Stderr = stderr.Text,
                    Truncated = stdout.Truncated || stderr.Truncated, State = command.Background ? SandboxExecutionState.Running : SandboxExecutionState.Completed,
                    ExitCode = execution.ExitCode, FinishedAt = command.Background ? null : time.GetUtcNow() };
                if (!command.Background) receipt = receipt with { DurationMs = Math.Max(0, execution.Complete?.ExecutionTimeMs ?? (long)(time.GetUtcNow() - receipt.StartedAt).TotalMilliseconds) };
                if (!command.Background)
                {
                    await ValidateAccessAsync(access, ct);
                    await ConfirmQuiescentAsync(sdk, lease, ct);
                    var transfer = await CollectAsync(sdk, lease.Profile, ct);
                    await ValidateAccessAsync(access, ct);
                    receipt = receipt with { WorkspaceSyncStatus = "committing" };
                    await SaveReceiptAsync(receipt, false, ct);
                    await workspace.CommitScratchAsync(access.SessionId, transfer, scratch.Fingerprint, ct, token => ValidateAccessAsync(access, token), receipt.Id);
                    scratchCommitted = true;
                    receipt = receipt with { WorkspaceSyncStatus = "committed" };
                    await SaveReceiptAsync(receipt, false, CancellationToken.None);
                    lease = await SaveAsync(lease with { State = SandboxLeaseState.Ready, ActiveExecutionId = null, LastActivityAt = time.GetUtcNow(), ExpiresAt = await RenewIdleAsync(sdk, lease, ct),
                        ScratchFingerprint = (await workspace.CaptureScratchAsync(access.SessionId, access.Definition, ct)).Fingerprint }, ct);
                }
                await SaveReceiptAsync(receipt, false, ct);
                return Result(receipt);
            }
            catch (Exception error)
            {
                if (error is AgentCoreException core) stderr.Append("\n" + core.Message);
                else stderr.Append("\nSandbox execution outcome could not be confirmed. Inspect the diagnostic and reset before retrying.");
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var terminated = await TryTerminateAsync(lease, cleanup.Token);
                receipt = receipt with { State = error is OperationCanceledException && terminated ? SandboxExecutionState.Canceled : SandboxExecutionState.Indeterminate,
                    Stdout = stdout.Text, Stderr = stderr.Text, Truncated = stdout.Truncated || stderr.Truncated,
                    FinishedAt = time.GetUtcNow(), WorkspaceSyncStatus = scratchCommitted ? "committed" : await CommitStatusAsync(receipt, CancellationToken.None), DiagnosticId = Guid.CreateVersion7().ToString("D") };
                // Even successful termination cannot imply uncertain effects were replay-safe.
                try
                {
                    lease = await SaveAsync(lease with { State = terminated ? SandboxLeaseState.NeedsResync : SandboxLeaseState.NeedsReconciliation,
                        DiagnosticId = receipt.DiagnosticId }, CancellationToken.None);
                    await SaveReceiptAsync(receipt, false, CancellationToken.None);
                }
                catch { /* Never mask an uncertain dispatched effect with a generic persistence error. The dispatch receipt remains for recovery. */ }
                if (error is OperationCanceledException) throw;
                return Result(receipt);
            }
        }
        finally { gate.Release(); }
    }

    private async ValueTask<SandboxLease> EnsureLockedAsync(SandboxAccess access, CancellationToken ct)
    {
        var lease = await store.GetLeaseAsync(access.SessionId, ct);
        if (lease is { State: SandboxLeaseState.Terminated, Initialization.State: "Running" or "Failed" } && lease.CleanupReason != "reset")
            throw AgentCoreErrors.Conflict("Initialization failed or its outcome is uncertain. Inspect its retained evidence and explicitly reset before retrying.");
        if (lease is not null && lease.AgentInstanceId != access.AgentInstanceId) throw AgentCoreErrors.NotFound("Sandbox was not found.");
        if (lease is not null && lease.State != SandboxLeaseState.Terminated)
        {
            if ((await Images.InspectAsync(lease.Profile, ct, requireVerification:false)).Status != "SourceAuthorized") throw AgentCoreErrors.Forbidden("Pinned generation source authority was revoked. Inspect and reset the Session sandbox.");
            if (lease.Profile.Id != access.Profile.Id) throw AgentCoreErrors.Conflict("Current sandbox uses another profile. Reset to use the newly selected profile.");
            if (lease.ExpiresAt <= time.GetUtcNow() && lease.ActiveExecutionId is null)
            {
                if (!await TryTerminateAsync(lease, ct)) throw AgentCoreErrors.Conflict("Sandbox expiration cleanup is pending.");
                lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminated }, ct);
            }
            else
            {
                if (lease.State == SandboxLeaseState.Ready && lease.ActiveExecutionId is null)
                {
                    try { await using var connected = await ConnectAsync(lease, ct); return lease; }
                    catch (SandboxApiException e) when (e.StatusCode == 404)
                    { lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminated }, ct); }
                }
                else return lease;
            }
        }
        var imageAuth = await Images.ResolveAuthAsync(access.Profile, ct, requireVerification: true);
        var now = time.GetUtcNow();
        var initialTtl = Math.Min(access.Profile.MaxLifetimeSeconds,
            Math.Max(access.Profile.IdleTtlSeconds, 45 + access.Profile.MaxCommandSeconds + CommitGraceSeconds));
        if ((!string.IsNullOrWhiteSpace(access.Profile.InitializationScript) || !string.IsNullOrWhiteSpace(access.Profile.InstanceInitializationScript)) && initialTtl < 45 + access.Profile.MaxCommandSeconds + CommitGraceSeconds)
            throw AgentCoreErrors.Conflict("Maximum sandbox lifetime cannot cover initialization. Increase it within the host ceiling.");
        lease = new(access.SessionId, access.AgentInstanceId, Guid.CreateVersion7(), (lease?.Revision ?? 0) + 1, access.Profile,
            SandboxLeaseState.Provisioning, null, now, now.AddSeconds(initialTtl),
            Initialization: new("Pending", access.Profile.Id, access.Profile.Revision, access.Profile.InstanceRevision));
        await store.SaveLeaseAsync(lease, lease.Revision - 1, ct);
        SdkSandbox? sdk = null;
        var phase = "provisioning";
        try
        {
            if (!OciImageReference.Parse(lease.Profile.ImageReference).IsLocal) await RegistryAuthorizationProbe.CheckAsync(options, OciImageReference.Parse(lease.Profile.ImageReference), imageAuth, ct);
            await ValidateAccessAsync(access, ct);
            imageAuth = await Images.ResolveAuthAsync(access.Profile,ct,requireVerification:true);
            sdk = await SdkSandbox.CreateAsync(new SandboxCreateOptions {
                ConnectionConfig = Connection(), Image = lease.Profile.ImageReference, ImageAuth = imageAuth, TimeoutSeconds = initialTtl, ReadyTimeoutSeconds = 45,
                Resource = new Dictionary<string,string> { ["cpu"] = lease.Profile.Cpu.ToString(CultureInfo.InvariantCulture), ["memory"] = lease.Profile.MemoryMiB + "Mi" },
                Metadata = new Dictionary<string,string> { ["agent-core-generation"] = lease.Generation.ToString("N") },
                NetworkPolicy = new NetworkPolicy { DefaultAction = NetworkRuleAction.Deny, Egress = [] }
            }, ct);
            imageAuth = null;
            lease = await SaveAsync(lease with { ProviderId = sdk.Id }, ct);
            var identity = await InternalCommandAsync(sdk, ["id", "-u"], ct);
            if (!int.TryParse(identity.Trim(), out var uid) || uid <= 0) throw new InvalidOperationException("Non-root execution is required.");
            var scratch = await workspace.CaptureScratchAsync(access.SessionId, access.Definition, ct);
            await HydrateAsync(sdk, scratch.Transfer, ct);
            lease = await SaveAsync(lease with { BaselineProcesses = await ProcessIdentitiesAsync(sdk, ct) }, ct);
            if (!string.IsNullOrWhiteSpace(lease.Profile.InitializationScript) || !string.IsNullOrWhiteSpace(lease.Profile.InstanceInitializationScript))
            {
                phase = "initialization";
                lease = await ReserveExecutionLifetimeAsync(sdk, lease, lease.Profile.MaxCommandSeconds, ct);
                lease = await InitializeLockedAsync(sdk, lease, access, scratch, ct);
                scratch = await workspace.CaptureScratchAsync(access.SessionId, access.Definition, ct);
            }
            else lease = await SaveAsync(lease with { Initialization = new("Succeeded", lease.Profile.Id, lease.Profile.Revision, lease.Profile.InstanceRevision,
                time.GetUtcNow(), time.GetUtcNow(), 0) }, ct);
            return await SaveAsync(lease with { State = SandboxLeaseState.Ready, InitializedAt = time.GetUtcNow(), LastActivityAt = time.GetUtcNow(), ExpiresAt = await RenewIdleAsync(sdk, lease, ct), ScratchFingerprint = scratch.Fingerprint }, ct);
        }
        catch (Exception error)
        {
            // Retain a safe cause even when orphan cleanup later reports pending.
            // SDK messages/bodies, provider IDs and credentials are never logged.
            var failure = error is SandboxApiException ? "http" : error is OperationCanceledException ? "canceled"
                : error is HttpRequestException ? "transport" : "setup";
            logger?.LogWarning("Sandbox provisioning failed. SessionId={SessionId} Generation={Generation} Failure={Failure} StatusCode={StatusCode} ProviderLinked={ProviderLinked}",
                lease.SessionId, lease.Generation, failure, (error as SandboxApiException)?.StatusCode, lease.ProviderId is not null);
            lease = await store.GetLeaseAsync(access.SessionId, CancellationToken.None) ?? lease;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var terminated = lease.ProviderId is not null && await TryTerminateAsync(lease, cleanup.Token);
            await SaveAsync(lease with { State = terminated ? SandboxLeaseState.Failed : SandboxLeaseState.NeedsReconciliation, DiagnosticId = Guid.CreateVersion7().ToString("D"),
                CleanupReason = phase == "initialization" ? "initializationFailed" : "setupFailed",
                SafeMessage = phase == "initialization" ? "Environment initialization failed. Correct and verify the profile script, then reset before retrying." : error is AgentCoreException sourceError ? sourceError.Message + " Inspect the saved source, then reset before retrying." : "Environment setup failed. Check private server readiness and the pinned image, then reset before retrying." }, CancellationToken.None);
            throw AgentCoreErrors.Persistence("Sandbox provisioning failed. Inspect readiness and reset before retrying.");
        }
        finally { if (sdk is not null) await sdk.DisposeAsync(); }
    }

    // Reserve command deadline plus bounded collection/import time, while
    // retaining the immutable profile's hard maximum lifetime.
    private const int CommitGraceSeconds = 60;
    private async ValueTask<SandboxLease> ReserveExecutionLifetimeAsync(SdkSandbox sdk, SandboxLease lease, int commandSeconds, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var lifetimeEnd = lease.CreatedAt.AddSeconds(lease.Profile.MaxLifetimeSeconds);
        var minimumEnd = now.AddSeconds(commandSeconds + CommitGraceSeconds);
        if (minimumEnd > lifetimeEnd)
            throw AgentCoreErrors.Conflict("Sandbox maximum lifetime cannot cover this command and file synchronization. Reset before starting it.");
        var target = now.AddSeconds(Math.Max(lease.Profile.IdleTtlSeconds, commandSeconds + CommitGraceSeconds));
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
    private async ValueTask<string> CommitStatusAsync(SandboxExecution execution, CancellationToken ct)
    {
        if (execution.WorkspaceSyncStatus == "committed") return "committed";
        if (execution.WorkspaceSyncStatus == "notCommitted") return "notCommitted";
        try
        {
            var status = await workspace.GetScratchCommitStatusAsync(execution.SessionId, execution.Id, ct);
            // Pre-journal receipts used pending throughout the import. Without
            // evidence they cannot prove that the older implementation never swapped.
            return execution.WorkspaceSyncStatus == "pending" && status == "notCommitted" ? "unknown" : status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return "unknown"; } // Missing/unreadable evidence can never prove absence of a commit.
    }

    private async Task<SdkSandbox> ConnectAsync(SandboxLease lease, CancellationToken ct)
    {
        if (lease.ProviderId is null) throw AgentCoreErrors.Conflict("Sandbox provisioning requires reconciliation.");
        var sdk = await SdkSandbox.ConnectAsync(new SandboxConnectOptions { SandboxId = lease.ProviderId, ConnectionConfig = Connection() }, ct);
        try
        {
            var info = await sdk.GetInfoAsync(ct);
            if (info.Metadata?.GetValueOrDefault("agent-core-generation") != lease.Generation.ToString("N"))
                throw AgentCoreErrors.NotFound("Sandbox generation was not found.");
            return sdk;
        }
        catch { await sdk.DisposeAsync(); throw; }
    }
    private async ValueTask<SandboxLease> SaveAsync(SandboxLease lease, CancellationToken ct)
    { var next = lease with { Revision = lease.Revision + 1 }; await store.SaveLeaseAsync(next, lease.Revision, ct); return next; }
    private async Task<bool> TryTerminateAsync(SandboxLease lease, CancellationToken ct)
    {
        if (lease.ProviderId is null) return false;
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
                await manager.KillSandboxAsync(lease.ProviderId, request.Token); return true;
            }
            catch (SandboxApiException e) when (e.StatusCode == 404) { return true; }
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
    public async ValueTask<SandboxEnvironmentInfo?> InspectAsync(Guid trustedSessionId, CancellationToken ct = default)
    {
        var lease = await store.GetLeaseAsync(trustedSessionId, ct);
        return lease is null ? null : new(lease.SessionId, lease.Generation, lease.State.ToString(), lease.Profile.Id, lease.Profile.Revision, lease.ExpiresAt, lease.ActiveExecutionId, lease.DiagnosticId, lease.LastExecutionId, lease.SafeMessage,
            lease.CreatedAt, lease.LastActivityAt ?? lease.InitializedAt ?? lease.CreatedAt, lease.CreatedAt.AddSeconds(lease.Profile.MaxLifetimeSeconds),
            lease.CleanupReason, lease.State is SandboxLeaseState.Terminating or SandboxLeaseState.NeedsReconciliation, lease.CleanupPasses,
            lease.LastCleanupAttemptAt, lease.Initialization, lease.Profile.Name, lease.Profile.ImageReference);
    }
    public async ValueTask StopSessionAsync(Guid trustedSessionId, CancellationToken ct = default, Guid? expectedGeneration = null)
    {
        var gate = Gate(trustedSessionId); await gate.WaitAsync(ct);
        try
        {
            var lease = await store.GetLeaseAsync(trustedSessionId, ct);
            if (expectedGeneration is not null && lease?.Generation != expectedGeneration) throw AgentCoreErrors.Conflict("Sandbox generation changed. Refresh before resetting.");
            if (lease is null) return;
            if (lease.State == SandboxLeaseState.Terminated)
            {
                if (expectedGeneration is not null && lease.CleanupReason != "reset")
                    await SaveAsync(lease with { CleanupReason = "reset" }, ct);
                return;
            }
            var metadata = await sessions.LoadMetadataAsync(trustedSessionId, ct);
            var reason = expectedGeneration is not null ? "reset" : metadata is null || metadata.DurablyDeletedAt is not null ? "sessionDeleted"
                : metadata.ArchivedAt is not null ? "sessionArchived" : metadata.Status is SessionStatus.Ending or SessionStatus.Ended ? "sessionEnded"
                : metadata.LifecycleStatus != SessionLifecycleStatus.Active ? "sessionDeactivated" : "sessionLifecycle";
            lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminating, CleanupReason = reason, CleanupPasses = lease.CleanupPasses + 1, LastCleanupAttemptAt = time.GetUtcNow() }, ct);
            if (!await TryTerminateAsync(lease, ct)) throw AgentCoreErrors.Persistence("Sandbox cleanup is pending; its durable receipt will be reconciled.");
            if (lease.ActiveExecutionId is { } id && await store.GetExecutionAsync(trustedSessionId, id, ct) is { State: SandboxExecutionState.Dispatching or SandboxExecutionState.Running } execution)
                await SaveReceiptAsync(execution with { State = SandboxExecutionState.Canceled, FinishedAt = time.GetUtcNow(), WorkspaceSyncStatus = await CommitStatusAsync(execution, ct) }, false, ct);
            await SaveAsync(lease with { State = SandboxLeaseState.Terminated, ActiveExecutionId = null }, ct);
        }
        finally { gate.Release(); }
    }
    public async ValueTask ReconcileAsync(CancellationToken ct = default)
    {
        foreach (var item in await store.ListLeasesAsync(ct))
        {
            var gate = Gate(item.SessionId); if (!await gate.WaitAsync(0, ct)) continue;
            try
            {
                var lease = await store.GetLeaseAsync(item.SessionId, ct); if (lease is null || lease.State == SandboxLeaseState.Terminated) continue;
                var session = await sessions.LoadMetadataAsync(lease.SessionId, ct);
                var revoked = (await Images.InspectAsync(lease.Profile, ct, requireVerification:false)).Status != "SourceAuthorized" || !options.Enabled || (await store.GetProfileAsync(lease.Profile.Id, ct: ct))?.Enabled != true
                    || instances is not null && (await instances.FindAsync(lease.AgentInstanceId,ct))?.Lifecycle != AgentInstanceLifecycle.Active;
                // Transport loss and application shutdown pause the conversation, but
                // retain an idle environment until TTL. Explicit deactivation cleans it.
                var idleDetached = session?.LifecycleStatus == SessionLifecycleStatus.Paused && session.LifecycleReason == "disconnected"
                    && lease.State == SandboxLeaseState.Ready && lease.ActiveExecutionId is null;
                if (session is null || session.DurablyDeletedAt is not null || session.ArchivedAt is not null || session.LifecycleStatus != SessionLifecycleStatus.Active && !idleDetached
                    || session.Status is SessionStatus.Ended or SessionStatus.Ending || revoked || lease.ExpiresAt <= time.GetUtcNow() || lease.State is SandboxLeaseState.Provisioning or SandboxLeaseState.Terminating or SandboxLeaseState.NeedsReconciliation)
                {
                    var reason = revoked ? "authorityRevoked" : lease.ExpiresAt <= time.GetUtcNow() ? (lease.CreatedAt.AddSeconds(lease.Profile.MaxLifetimeSeconds) <= time.GetUtcNow() ? "maximumLifetime" : "idleExpired")
                        : lease.Initialization?.State is "Pending" or "Running" or "Failed" ? "initializationUncertain" : "sessionLifecycle";
                    lease = await SaveAsync(lease with { CleanupReason = reason, CleanupPasses = lease.CleanupPasses + 1, LastCleanupAttemptAt = time.GetUtcNow() }, ct);
                    if (lease.ProviderId is null)
                    {
                        // Adopt nothing. Only clean remote candidates linked to this durable generation.
                        await using var manager = SandboxManager.Create(new SandboxManagerOptions { ConnectionConfig = Connection() });
                        var page = await manager.ListSandboxInfosAsync(new SandboxFilter { Metadata = new Dictionary<string,string> { ["agent-core-generation"] = lease.Generation.ToString("N") }, PageSize = 100 }, ct);
                        foreach (var remote in page.Items) await manager.KillSandboxAsync(remote.Id, ct);
                        await FinalizeCleanupAsync(lease, ct);
                    }
                    else if (await TryTerminateAsync(lease, ct)) await FinalizeCleanupAsync(lease, ct);
                }
                else if (lease.ActiveExecutionId is { } executionId)
                {
                    var execution = await store.GetExecutionAsync(lease.SessionId, executionId, ct);
                    if (execution is { WorkspaceSyncStatus: "committing" })
                    {
                        var killed = await TryTerminateAsync(lease, ct);
                        await SaveReceiptAsync(execution with { State = SandboxExecutionState.Indeterminate, FinishedAt = time.GetUtcNow(), WorkspaceSyncStatus = await CommitStatusAsync(execution, ct) }, false, ct);
                        await SaveAsync(lease with { State = killed ? SandboxLeaseState.NeedsResync : SandboxLeaseState.NeedsReconciliation }, ct);
                        continue;
                    }
                    if (execution is { Background: false, State: SandboxExecutionState.Dispatching or SandboxExecutionState.Running } && session?.ProfileId is { } ownerProfile)
                    {
                        var run = await runs.GetAsync(new(lease.AgentInstanceId,ownerProfile),execution.AgentRunId,ct);
                        if (run?.Claim?.Generation != execution.ClaimGeneration || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow() || run.CancellationRequested || (execution.DeadlineAt ?? execution.StartedAt.AddSeconds(lease.Profile.MaxCommandSeconds)) <= time.GetUtcNow())
                        {
                            var killed = await TryTerminateAsync(lease,ct);
                            await SaveReceiptAsync(execution with { State = SandboxExecutionState.Indeterminate, FinishedAt = time.GetUtcNow(), WorkspaceSyncStatus = await CommitStatusAsync(execution, ct) },false,ct);
                            await SaveAsync(lease with { State = killed ? SandboxLeaseState.NeedsResync : SandboxLeaseState.NeedsReconciliation },ct);
                            continue;
                        }
                    }
                    if (execution is { State: SandboxExecutionState.Completed or SandboxExecutionState.Canceled or SandboxExecutionState.Indeterminate })
                    {
                        // The receipt is saved before clearing the lease. Recover
                        // an interrupted lease update without reimporting any tree.
                        var status = await CommitStatusAsync(execution, ct);
                        await SaveReceiptAsync(execution with { WorkspaceSyncStatus = status }, false, ct);
                        await SaveAsync(lease with { ActiveExecutionId = null, State = status == "committed" ? SandboxLeaseState.Ready : SandboxLeaseState.NeedsResync }, ct);
                    }
                    else if (execution is { Background: true })
                    {
                        var ownerRun = session?.ProfileId is { } profileId ? await runs.GetAsync(new(lease.AgentInstanceId, profileId), execution.AgentRunId, ct) : null;
                        if (ownerRun is null || ownerRun.CancellationRequested || ownerRun.Status is AgentRunStatus.Cancelled or AgentRunStatus.Failed
                            || (execution.DeadlineAt ?? execution.StartedAt.AddSeconds(lease.Profile.MaxCommandSeconds)) <= time.GetUtcNow())
                        {
                            if (await TryTerminateAsync(lease, ct)) await FinalizeCleanupAsync(lease, ct);
                        }
                        else await RefreshProcessLockedAsync(lease, execution, false, ct);
                    }
                }
            }
            catch { /* Preserve the durable cleanup/uncertainty receipt for the next bounded pass. */ }
            finally { gate.Release(); }
        }
    }
    private async ValueTask FinalizeCleanupAsync(SandboxLease lease, CancellationToken ct)
    {
        if (lease.ActiveExecutionId is { } id && await store.GetExecutionAsync(lease.SessionId,id,ct) is { State:SandboxExecutionState.Dispatching or SandboxExecutionState.Running } execution)
            await SaveReceiptAsync(execution with { State=SandboxExecutionState.Indeterminate,FinishedAt=time.GetUtcNow(),WorkspaceSyncStatus=await CommitStatusAsync(execution, ct) },false,ct);
        await SaveAsync(lease with { State=SandboxLeaseState.Terminated,ActiveExecutionId=null },ct);
    }
    private static SandboxCommandResult Result(SandboxExecution e) => new(e.Id, e.State.ToString(), e.ExitCode, e.Stdout, e.Stderr, e.Truncated,
        e.DurationMs ?? (e.FinishedAt is { } end ? Math.Max(0, (long)(end - e.StartedAt).TotalMilliseconds) : 0), e.WorkspaceSyncStatus, e.DiagnosticId, e.Background, e.CommandText);
    private sealed class BoundedText(int maxBytes)
    {
        private readonly StringBuilder _text = new(); private int _bytes;
        public bool Truncated { get; private set; } public string Text => _text.ToString();
        public void Append(string value) { foreach (var rune in value.EnumerateRunes()) { if (_bytes + rune.Utf8SequenceLength > maxBytes) { Truncated = true; return; } _text.Append(rune.ToString()); _bytes += rune.Utf8SequenceLength; } }
    }
}
