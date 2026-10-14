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
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

/// <summary>One SDK adapter; authoritative files and durable ownership remain in Core.</summary>
public sealed partial class OpenSandboxService(OpenSandboxOptions options, ISandboxStore store,
    ISessionWorkspace workspace, IMemoryStore sessions, IAgentRunStore runs, TimeProvider time, IAgentInstanceStore? instances = null) : ISandboxService
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private SemaphoreSlim Gate(Guid id) => _gates.GetOrAdd(id, _ => new(1, 1));
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
        var allowed = access.Definition.SandboxPolicy?.AllowedProfileIds;
        if (allowed is not null && !allowed.Contains(access.Profile.Id, StringComparer.Ordinal))
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
            var now = time.GetUtcNow();
            var targetExpiry = now.AddSeconds(lease.Profile.IdleTtlSeconds);
            var lifetimeEnd = lease.CreatedAt.AddSeconds(lease.Profile.MaxLifetimeSeconds);
            if (targetExpiry > lifetimeEnd) targetExpiry = lifetimeEnd;
            if (targetExpiry > lease.ExpiresAt.AddSeconds(1))
            {
                await sdk.RenewAsync(Math.Max(1, (int)(targetExpiry - now).TotalSeconds), ct);
                lease = await SaveAsync(lease with { ExpiresAt = targetExpiry }, ct);
            }
            var scratch = await workspace.CaptureScratchAsync(access.SessionId, access.Definition, ct);
            if (scratch.Fingerprint != lease.ScratchFingerprint) await HydrateAsync(sdk, scratch.Transfer, ct);
            var receipt = new SandboxExecution(Guid.CreateVersion7(), access.SessionId, access.AgentInstanceId, lease.Generation,
                access.AgentRunId, access.ClaimGeneration, access.ToolCallId, hash, SandboxExecutionState.Dispatching, time.GetUtcNow(), Background: command.Background,
                CommandText: command.Command ?? JsonSerializer.Serialize(command.Argv, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            await store.SaveExecutionAsync(receipt, true, ct);
            lease = await SaveAsync(lease with { State = SandboxLeaseState.Executing, ActiveExecutionId = receipt.Id, LastExecutionId = receipt.Id, ScratchFingerprint = scratch.Fingerprint }, ct);
            var stdout = new BoundedText(32768); var stderr = new BoundedText(32768);
            var lastOutputSave = time.GetUtcNow().AddSeconds(-1);
            async Task SaveOutputAsync()
            {
                if (time.GetUtcNow() - lastOutputSave < TimeSpan.FromSeconds(1)) return;
                lastOutputSave = time.GetUtcNow();
                receipt = receipt with { Stdout = stdout.Text, Stderr = stderr.Text, Truncated = stdout.Truncated || stderr.Truncated };
                await store.SaveExecutionAsync(receipt, false, ct);
            }
            var scratchCommitted = false;
            try
            {
                var timeout = Math.Min(command.TimeoutSeconds, lease.Profile.MaxCommandSeconds);
                var handlers = new ExecutionHandlers {
                    SkipAccumulation = true,
                    OnStdout = async m => { stdout.Append(m.Text); await SaveOutputAsync(); },
                    OnStderr = async m => { stderr.Append(m.Text); await SaveOutputAsync(); },
                    OnInit = async init => {
                        receipt = receipt with { ProviderCommandId = init.Id, State = SandboxExecutionState.Running };
                        await store.SaveExecutionAsync(receipt, false, ct);
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
                    await workspace.CommitScratchAsync(access.SessionId, transfer, scratch.Fingerprint, ct, token => ValidateAccessAsync(access, token));
                    scratchCommitted = true;
                    receipt = receipt with { WorkspaceSyncStatus = "committed" };
                    lease = await SaveAsync(lease with { State = SandboxLeaseState.Ready, ActiveExecutionId = null,
                        ScratchFingerprint = (await workspace.CaptureScratchAsync(access.SessionId, access.Definition, ct)).Fingerprint }, ct);
                }
                await store.SaveExecutionAsync(receipt, false, ct);
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
                    FinishedAt = time.GetUtcNow(), WorkspaceSyncStatus = scratchCommitted ? "committed" : "notCommitted", DiagnosticId = Guid.CreateVersion7().ToString("D") };
                // Even successful termination cannot imply uncertain effects were replay-safe.
                try
                {
                    lease = await SaveAsync(lease with { State = terminated ? SandboxLeaseState.NeedsResync : SandboxLeaseState.NeedsReconciliation,
                        DiagnosticId = receipt.DiagnosticId }, CancellationToken.None);
                    await store.SaveExecutionAsync(receipt, false, CancellationToken.None);
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
        if (lease is not null && lease.AgentInstanceId != access.AgentInstanceId) throw AgentCoreErrors.NotFound("Sandbox was not found.");
        if (lease is not null && lease.State != SandboxLeaseState.Terminated)
        {
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
        var now = time.GetUtcNow();
        lease = new(access.SessionId, access.AgentInstanceId, Guid.CreateVersion7(), (lease?.Revision ?? 0) + 1, access.Profile,
            SandboxLeaseState.Provisioning, null, now, now.AddSeconds(access.Profile.IdleTtlSeconds));
        await store.SaveLeaseAsync(lease, lease.Revision - 1, ct);
        SdkSandbox? sdk = null;
        try
        {
            sdk = await SdkSandbox.CreateAsync(new SandboxCreateOptions {
                ConnectionConfig = Connection(), Image = lease.Profile.ImageReference, TimeoutSeconds = lease.Profile.IdleTtlSeconds, ReadyTimeoutSeconds = 45,
                Resource = new Dictionary<string,string> { ["cpu"] = lease.Profile.Cpu.ToString(CultureInfo.InvariantCulture), ["memory"] = lease.Profile.MemoryMiB + "Mi" },
                Metadata = new Dictionary<string,string> { ["agent-core-generation"] = lease.Generation.ToString("N") },
                NetworkPolicy = new NetworkPolicy { DefaultAction = NetworkRuleAction.Deny, Egress = [] }
            }, ct);
            lease = await SaveAsync(lease with { ProviderId = sdk.Id }, ct);
            var identity = await InternalCommandAsync(sdk, ["id", "-u"], ct);
            if (!int.TryParse(identity.Trim(), out var uid) || uid <= 0) throw new InvalidOperationException("Non-root execution is required.");
            var scratch = await workspace.CaptureScratchAsync(access.SessionId, access.Definition, ct);
            await HydrateAsync(sdk, scratch.Transfer, ct);
            lease = await SaveAsync(lease with { BaselineProcesses = await ProcessIdentitiesAsync(sdk, ct) }, ct);
            if (!string.IsNullOrWhiteSpace(lease.Profile.InitializationScript))
            {
                var init = await sdk.Commands.RunAsync(lease.Profile.InitializationScript, new RunCommandOptions { WorkingDirectory = "/working", TimeoutSeconds = lease.Profile.MaxCommandSeconds },
                    new ExecutionHandlers { SkipAccumulation = true }, ct);
                if (init.ExitCode != 0) throw new InvalidOperationException("Initialization failed.");
                await ConfirmQuiescentAsync(sdk, lease, ct);
                await ValidateAccessAsync(access, ct);
                await workspace.CommitScratchAsync(access.SessionId, await CollectAsync(sdk, lease.Profile, ct), scratch.Fingerprint, ct, token => ValidateAccessAsync(access, token));
                scratch = await workspace.CaptureScratchAsync(access.SessionId, access.Definition, ct);
            }
            return await SaveAsync(lease with { State = SandboxLeaseState.Ready, InitializedAt = time.GetUtcNow(), ScratchFingerprint = scratch.Fingerprint }, ct);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var terminated = lease.ProviderId is not null && await TryTerminateAsync(lease, cleanup.Token);
            await SaveAsync(lease with { State = terminated ? SandboxLeaseState.Failed : SandboxLeaseState.NeedsReconciliation, DiagnosticId = Guid.CreateVersion7().ToString("D") }, CancellationToken.None);
            throw AgentCoreErrors.Persistence("Sandbox provisioning failed. Inspect readiness and reset before retrying.");
        }
        finally { if (sdk is not null) await sdk.DisposeAsync(); }
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
        try { await using var manager = SandboxManager.Create(new SandboxManagerOptions { ConnectionConfig = Connection() }); await manager.KillSandboxAsync(lease.ProviderId, budget.Token); return true; }
        catch (SandboxApiException e) when (e.StatusCode == 404) { return true; }
        catch { return false; }
    }
    public async ValueTask<SandboxEnvironmentInfo?> InspectAsync(Guid trustedSessionId, CancellationToken ct = default)
    {
        var lease = await store.GetLeaseAsync(trustedSessionId, ct);
        return lease is null ? null : new(lease.SessionId, lease.Generation, lease.State.ToString(), lease.Profile.Id, lease.Profile.Revision, lease.ExpiresAt, lease.ActiveExecutionId, lease.DiagnosticId, lease.LastExecutionId);
    }
    public async ValueTask StopSessionAsync(Guid trustedSessionId, CancellationToken ct = default, Guid? expectedGeneration = null)
    {
        var gate = Gate(trustedSessionId); await gate.WaitAsync(ct);
        try
        {
            var lease = await store.GetLeaseAsync(trustedSessionId, ct);
            if (expectedGeneration is not null && lease?.Generation != expectedGeneration) throw AgentCoreErrors.Conflict("Sandbox generation changed. Refresh before resetting.");
            if (lease is null || lease.State == SandboxLeaseState.Terminated) return;
            lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminating }, ct);
            if (!await TryTerminateAsync(lease, ct)) throw AgentCoreErrors.Persistence("Sandbox cleanup is pending; its durable receipt will be reconciled.");
            if (lease.ActiveExecutionId is { } id && await store.GetExecutionAsync(trustedSessionId, id, ct) is { State: SandboxExecutionState.Dispatching or SandboxExecutionState.Running } execution)
                await store.SaveExecutionAsync(execution with { State = SandboxExecutionState.Canceled, FinishedAt = time.GetUtcNow(), WorkspaceSyncStatus = "notCommitted" }, false, ct);
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
                var revoked = !options.Enabled || (await store.GetProfileAsync(lease.Profile.Id, ct: ct))?.Enabled != true
                    || instances is not null && (await instances.FindAsync(lease.AgentInstanceId,ct))?.Lifecycle != AgentInstanceLifecycle.Active;
                // Transport loss and application shutdown pause the conversation, but
                // retain an idle environment until TTL. Explicit deactivation cleans it.
                var idleDetached = session?.LifecycleStatus == SessionLifecycleStatus.Paused && session.LifecycleReason == "disconnected"
                    && lease.State == SandboxLeaseState.Ready && lease.ActiveExecutionId is null;
                if (session is null || session.DurablyDeletedAt is not null || session.ArchivedAt is not null || session.LifecycleStatus != SessionLifecycleStatus.Active && !idleDetached
                    || session.Status is SessionStatus.Ended or SessionStatus.Ending || revoked || lease.ExpiresAt <= time.GetUtcNow() || lease.State is SandboxLeaseState.Provisioning or SandboxLeaseState.Terminating or SandboxLeaseState.NeedsReconciliation)
                {
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
                    if (execution is { Background: false } && session?.ProfileId is { } ownerProfile)
                    {
                        var run = await runs.GetAsync(new(lease.AgentInstanceId,ownerProfile),execution.AgentRunId,ct);
                        if (run?.Claim?.Generation != execution.ClaimGeneration || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow() || run.CancellationRequested)
                        {
                            var killed = await TryTerminateAsync(lease,ct);
                            await store.SaveExecutionAsync(execution with { State = SandboxExecutionState.Indeterminate, FinishedAt = time.GetUtcNow(), WorkspaceSyncStatus = "notCommitted" },false,ct);
                            await SaveAsync(lease with { State = killed ? SandboxLeaseState.NeedsResync : SandboxLeaseState.NeedsReconciliation },ct);
                            continue;
                        }
                    }
                    if (execution is not null) await RefreshProcessLockedAsync(lease, execution, false, ct);
                }
            }
            catch { /* Preserve the durable cleanup/uncertainty receipt for the next bounded pass. */ }
            finally { gate.Release(); }
        }
    }
    private async ValueTask FinalizeCleanupAsync(SandboxLease lease, CancellationToken ct)
    {
        if (lease.ActiveExecutionId is { } id && await store.GetExecutionAsync(lease.SessionId,id,ct) is { State:SandboxExecutionState.Dispatching or SandboxExecutionState.Running } execution)
            await store.SaveExecutionAsync(execution with { State=SandboxExecutionState.Indeterminate,FinishedAt=time.GetUtcNow(),WorkspaceSyncStatus="notCommitted" },false,ct);
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
