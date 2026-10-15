using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
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
    ILogger<OpenSandboxService>? logger = null, SandboxImageAuthority? imageAuthority = null, IAgentRunAuthority? runAuthority = null) : ISandboxService
{
    private SandboxImageAuthority Images => imageAuthority ?? new(options, store, time);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private SemaphoreSlim Gate(Guid id) => _gates.GetOrAdd(id, _ => new(1, 1));
    internal Action<SandboxExecution>? ExecutionReceiptFault { get; set; }
    internal Action<string>? FinalizationCheckpoint { get; set; }
    internal sealed class FinalizationCrash : Exception { }
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
    private async ValueTask ValidateAccessAsync(SandboxAccess access, CancellationToken ct, bool execution = true)
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
        if (!execution) return;
        await ValidateLiveProfileAuthorityAsync(run, access.Profile, ct);
        if (instances is not null && (await instances.FindAsync(access.AgentInstanceId, ct))?.SettingsOverrides?.SandboxAllowedProfileIds is { } narrowed
            && !narrowed.Value.Contains(access.Profile.Id, StringComparer.Ordinal))
            throw AgentCoreErrors.Forbidden("Instance profile authority was narrowed after Run admission.");
        if (access.Profile.AdmittedForExecution == false)
            throw AgentCoreErrors.Forbidden("This profile was not execution-eligible when the Run was admitted. Start a new Run after repair; current authority was not expanded.");
        if (!options.Enabled || (await store.GetProfileAsync(access.Profile.Id, ct: ct))?.Enabled != true)
            throw AgentCoreErrors.Forbidden("Sandbox profile is disabled or unavailable.");
        options.ValidateProfile(access.Profile);
        var source = await Images.InspectAsync(access.Profile, ct, requireVerification: false);
        if (source.Status != "SourceAuthorized") throw AgentCoreErrors.Forbidden(source.Message);
        var eligibility = await Images.InspectAsync(access.Profile, ct);
        if (!eligibility.Eligible || eligibility.CredentialRevision != access.Profile.RegistryCredentialRevision || eligibility.SecretRevision != access.Profile.RegistrySecretRevision)
            throw AgentCoreErrors.Forbidden("Selected profile verification or source authority changed. Inspect current profiles and start a new Run after repair.");
        var allowed = access.Definition.SandboxPolicy?.AllowedProfileIds;
        if (allowed is null || !allowed.Contains(access.Profile.Id, StringComparer.Ordinal))
            throw AgentCoreErrors.Forbidden("Definition does not allow this sandbox profile.");
    }

    private async ValueTask ValidateLiveProfileAuthorityAsync(AgentRun run, SandboxProfile profile, CancellationToken ct)
    {
        if (runAuthority is null) return;
        var current = await runAuthority.CurrentDefinitionAsync(run, ct);
        if (current?.SandboxPolicy?.AllowedProfileIds.Contains(profile.Id, StringComparer.Ordinal) != true
            || !RolePermissions.AllowsTool(current, ToolCatalog.SandboxExec))
            throw AgentCoreErrors.Forbidden("Current Definition or Instance authority no longer permits this sandbox profile.");
    }

    public async ValueTask<SandboxCommandResult> ExecuteAsync(SandboxAccess access, SandboxCommandRequest command, CancellationToken ct = default)
    {
        SandboxCommands.Validate(command);
        var gate = Gate(access.SessionId);
        if (!await gate.WaitAsync(0, ct))
        {
            await ValidateAccessAsync(access, ct, execution: false);
            var busy = await store.GetLeaseAsync(access.SessionId, ct);
            var result = busy is null ? new SandboxCommandResult(Guid.Empty, "Busy", null, "", "", false, 0, "unknown", Error: "recovery_required", EnvironmentState: "Busy")
                : await RecoveryRequiredAsync(busy, ct);
            return result with { Recovery = "Another command, cleanup or profile transition holds this Session's execution gate. No command was queued or dispatched. Inspect current evidence and wait for it to settle; do not bypass it by selecting another profile." };
        }
        try
        {
            await ValidateAccessAsync(access, ct, execution: false);
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command))).ToLowerInvariant();
            var previous = await store.GetOperationAsync(access.SessionId, access.AgentRunId, access.ToolCallId, ct);
            if (previous is not null)
            {
                if (previous.ActionHash != hash) throw AgentCoreErrors.Conflict("Tool operation already names another command.");
                var environment = await store.GetLeaseAsync(access.SessionId, ct);
                return environment is null ? Result(previous) : FinalizedResult(environment, previous); // Observation, never permission to repeat.
            }
            var observed = await store.GetLeaseAsync(access.SessionId, ct);
            if (observed is not null && (observed.State is SandboxLeaseState.NeedsResync or SandboxLeaseState.NeedsReconciliation or SandboxLeaseState.Terminating or SandboxLeaseState.Failed or SandboxLeaseState.Provisioning
                || observed.ActiveExecutionId is not null)) return await RecoveryRequiredAsync(observed, ct);
            var selectedId = command.ProfileId ?? (observed?.CleanupReason == "reset" ? null : observed?.TransitionProfileId ?? observed?.SelectedProfileId ?? observed?.Profile.Id)
                ?? access.Definition.SandboxPolicy?.DefaultProfileId ?? access.Profile.Id;
            var pinned = access.Definition.ExecutionSandboxProfiles;
            var selected = pinned is null ? access.Profile.Id == selectedId ? access.Profile : null : pinned.FirstOrDefault(p => p.Id == selectedId);
            if (selected is null) throw AgentCoreErrors.Forbidden("Profile is not admitted to this Run. Discover authorized profiles or start a new Run; no fallback was selected.");
            if (command.ProfileId is null && observed?.TransitionProfileId is not null && observed.TransitionProfileRevision != selected.Revision)
                throw AgentCoreErrors.Conflict("The pending switch pinned another profile revision. Inspect and explicitly select the new authorized revision or reset; no command was dispatched.");
            access = access with { Profile = selected };
            await ValidateAccessAsync(access, ct);
            try { Mounts.ValidateConfiguration(); }
            catch { throw AgentCoreErrors.Conflict("Sandbox workspace mount configuration is unavailable or incompatible. Restore the updated launcher handoff before executing; no command was dispatched."); }
            SandboxLease lease;
            try { lease = await EnsureLockedAsync(access, ct); }
            catch (Exception) when (!ct.IsCancellationRequested && observed is not null)
            {
                var interrupted = await store.GetLeaseAsync(access.SessionId, CancellationToken.None);
                if (interrupted?.TransitionPhase is not null || interrupted?.State is SandboxLeaseState.NeedsReconciliation or SandboxLeaseState.Failed)
                    return await RecoveryRequiredAsync(interrupted!, CancellationToken.None);
                throw;
            }
            if (lease.State != SandboxLeaseState.Ready || lease.ActiveExecutionId is not null)
                throw AgentCoreErrors.Conflict("Sandbox requires process inspection, stop or reset before another command.");
            await using var sdk = await ConnectAsync(lease, ct);
            var timeout = Math.Min(command.TimeoutSeconds ?? lease.Profile.MaxCommandSeconds, lease.Profile.MaxCommandSeconds);
            var scratch = await workspace.CaptureScratchAsync(access.SessionId, access.Definition, ct);
            if (scratch.Fingerprint != lease.ScratchFingerprint) await HydrateAsync(lease, scratch.Transfer, ct);
            lease = await ReserveExecutionLifetimeAsync(sdk, lease, timeout, ct);
            var receipt = new SandboxExecution(Guid.CreateVersion7(), access.SessionId, access.AgentInstanceId, lease.Generation,
                access.AgentRunId, access.ClaimGeneration, access.ToolCallId, hash, SandboxExecutionState.Dispatching, time.GetUtcNow(), Background: command.Background, ProfileId: lease.Profile.Id, ProfileRevision: lease.Profile.Revision, Phase: "dispatch",
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
            try
            {
                var handlers = new ExecutionHandlers {
                    SkipAccumulation = true,
                    OnStdout = async m => { stdout.Append(m.Text); await SaveOutputAsync(); },
                    OnStderr = async m => { stderr.Append(m.Text); await SaveOutputAsync(); },
                    OnInit = async init => {
                        receipt = receipt with { ProviderCommandId = init.Id, State = SandboxExecutionState.Running, Phase = "execution" };
                        await SaveReceiptAsync(receipt, false, ct);
                    }
                };
                var opts = new RunCommandOptions { WorkingDirectory = command.Cwd, Background = command.Background, TimeoutSeconds = timeout };
                var execution = await GuardCommandStorageAsync(lease, token => command.Command is { } shell
                    ? sdk.Commands.RunAsync(shell, opts, handlers, token)
                    : sdk.Commands.RunAsync(command.Argv!, opts, handlers, token), ct);
                if (!command.Background && (execution.ExitCode is null or < 0 or > 255
                    || execution.Error?.Name?.Contains("Timeout",StringComparison.OrdinalIgnoreCase) == true))
                    throw execution.Error?.Name?.Contains("Timeout", StringComparison.OrdinalIgnoreCase) == true
                        ? new TimeoutException("Command timeout; termination requires confirmation.")
                        : new InvalidOperationException("Command termination was not confirmed.");
                receipt = receipt with { ProviderCommandId = execution.Id ?? receipt.ProviderCommandId, Stdout = stdout.Text, Stderr = stderr.Text,
                    Truncated = stdout.Truncated || stderr.Truncated, State = command.Background ? SandboxExecutionState.Running : SandboxExecutionState.Completed,
                    ExitCode = execution.ExitCode, FinishedAt = command.Background ? null : time.GetUtcNow() };
                if (!command.Background) receipt = receipt with { DurationMs = Math.Max(0, execution.Complete?.ExecutionTimeMs ?? (long)(time.GetUtcNow() - receipt.StartedAt).TotalMilliseconds) };
                if (!command.Background)
                {
                    receipt = receipt with { Phase = "processInspection" };
                    await SaveReceiptAsync(receipt, false, ct); // Completion survives a crash before retirement/import.
                    FinalizationCheckpoint?.Invoke("commandCompleted");
                    (lease, receipt) = await FinalizeCompletedExecutionAsync(sdk, lease, receipt, access.Definition, token => ValidateAccessAsync(access, token), ct);
                    return FinalizedResult(lease, receipt);
                }
                await SaveReceiptAsync(receipt, false, ct);
                return Result(receipt);
            }
            catch (Exception error) when (error is not FinalizationCrash)
            {
                if (error is AgentCoreException core) stderr.Append("\n" + core.Message);
                else stderr.Append("\nSandbox finalization could not be confirmed. Inspect the retained command, file and cleanup evidence; do not replay automatically.");
                try
                {
                    (lease, receipt) = await AbortExecutionFinalizationAsync(lease, receipt, error);
                    receipt = receipt with { Stdout = stdout.Text, Stderr = stderr.Text, Truncated = stdout.Truncated || stderr.Truncated };
                    await SaveReceiptAsync(receipt, false, CancellationToken.None);
                }
                catch
                {
                    if (error is OperationCanceledException) throw;
                    return await UnsettledFailureResultAsync(lease, receipt);
                }
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
            if (lease.Profile.Id != access.Profile.Id || lease.Profile.Revision != access.Profile.Revision
                || lease.Profile.RegistryCredentialId != access.Profile.RegistryCredentialId
                || lease.Profile.RegistryCredentialRevision != access.Profile.RegistryCredentialRevision
                || lease.Profile.RegistrySecretRevision != access.Profile.RegistrySecretRevision
                || lease.Profile.InstanceInitializationScript != access.Profile.InstanceInitializationScript
                || lease.Profile.Cpu != access.Profile.Cpu || lease.Profile.MemoryMiB != access.Profile.MemoryMiB
                || lease.Profile.MaxCommandSeconds != access.Profile.MaxCommandSeconds || lease.Profile.StorageBytes != access.Profile.StorageBytes
                || lease.Profile.IdleTtlSeconds != access.Profile.IdleTtlSeconds || lease.Profile.MaxLifetimeSeconds != access.Profile.MaxLifetimeSeconds)
            {
                // Resolve all target authority before touching the old generation.
                var targetAuth = await Images.ResolveAuthAsync(access.Profile, ct, requireVerification: true);
                var targetSource = OciImageReference.Parse(access.Profile.ImageReference);
                if (!targetSource.IsLocal) await RegistryAuthorizationProbe.CheckAsync(options, targetSource, targetAuth, ct);
                targetAuth = null;
                if (lease.State != SandboxLeaseState.Ready || lease.ActiveExecutionId is not null)
                    throw AgentCoreErrors.Conflict("Switching requires a quiescent generation with resolved files and cleanup.");
                await using (var connected = await ConnectAsync(lease, ct)) await ConfirmQuiescentAsync(connected, lease, ct);
                if (lease.LastExecutionId is { } previousId && await store.GetExecutionAsync(lease.SessionId, previousId, ct) is { } previous
                    && await CommitStatusAsync(previous, ct) is not ("committed" or "notCommitted"))
                    throw AgentCoreErrors.Conflict("Switching is blocked by an unresolved canonical commit. Inspect and explicitly reset.");
                await ValidateAccessAsync(access, ct);
                lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminating, CleanupReason = "profileSwitch",
                    TransitionProfileId = access.Profile.Id, TransitionProfileRevision = access.Profile.Revision, TransitionPhase = "termination", AdmissionRunId = access.AgentRunId, AdmissionToolCallId = access.ToolCallId }, ct);
                if (!await TryTerminateAsync(lease, ct)) throw AgentCoreErrors.Conflict("Profile switch termination is pending. Inspect cleanup; no command was dispatched.");
                lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminated, ActiveExecutionId = null, TransitionPhase = "provisioning" }, ct);
            }
            if (lease.State == SandboxLeaseState.Terminated) { /* A validated switch continues into provisioning below. */ }
            else if (lease.ExpiresAt <= time.GetUtcNow() && lease.ActiveExecutionId is null)
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
                    {
                        if (lease.MountContractVersion == 1) Mounts.Delete(lease.Generation);
                        lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminated }, ct);
                    }
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
            Initialization: new("Pending", access.Profile.Id, access.Profile.Revision, access.Profile.InstanceRevision), MountContractVersion: 1,
            SelectedProfileId: access.Profile.Id, TransitionPhase: "provisioning", AdmissionRunId: access.AgentRunId, AdmissionToolCallId: access.ToolCallId);
        await store.SaveLeaseAsync(lease, lease.Revision - 1, ct);
        SdkSandbox? sdk = null;
        var phase = "provisioning";
        try
        {
            if (!OciImageReference.Parse(lease.Profile.ImageReference).IsLocal) await RegistryAuthorizationProbe.CheckAsync(options, OciImageReference.Parse(lease.Profile.ImageReference), imageAuth, ct);
            await ValidateAccessAsync(access, ct);
            imageAuth = await Images.ResolveAuthAsync(access.Profile,ct,requireVerification:true);
            phase = "mountPreparation";
            var workingVolume = Mounts.Prepare(lease.Generation);
            phase = "provisioning";
            sdk = await SdkSandbox.CreateAsync(new SandboxCreateOptions {
                ConnectionConfig = Connection(), Image = lease.Profile.ImageReference, ImageAuth = imageAuth, TimeoutSeconds = initialTtl, ReadyTimeoutSeconds = 45,
                Resource = new Dictionary<string,string> { ["cpu"] = lease.Profile.Cpu.ToString(CultureInfo.InvariantCulture), ["memory"] = lease.Profile.MemoryMiB + "Mi" },
                Metadata = new Dictionary<string,string> { ["agent-core-generation"] = lease.Generation.ToString("N") },
                Volumes = [workingVolume],
                NetworkPolicy = new NetworkPolicy { DefaultAction = NetworkRuleAction.Deny, Egress = [] }
            }, ct);
            imageAuth = null;
            lease = await SaveAsync(lease with { ProviderId = sdk.Id }, ct);
            phase = "mountValidation";
            await CheckImageReadinessAsync(sdk, lease.Generation, ct);
            phase = "hydration";
            var scratch = await workspace.CaptureScratchAsync(access.SessionId, access.Definition, ct);
            await HydrateAsync(lease, scratch.Transfer, ct);
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
            return await SaveAsync(lease with { State = SandboxLeaseState.Ready, TransitionPhase = null, InitializedAt = time.GetUtcNow(), LastActivityAt = time.GetUtcNow(), ExpiresAt = await RenewIdleAsync(sdk, lease, ct), ScratchFingerprint = scratch.Fingerprint }, ct);
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
                CleanupReason = phase == "initialization" ? "initializationFailed" : "setupFailed", FailurePhase = phase,
                FailureCategory = error is AgentCoreException { Code: "WorkspaceQuotaExceeded" } ? "storageLimit" : error is OperationCanceledException ? "cancellation" : error is AgentCoreException { Code: "Forbidden" } ? "sourceAuthorizationChanged"
                    : phase.StartsWith("mount", StringComparison.Ordinal) ? "mountFailure" : "provisioningFailure",
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
        if (lease.MountContractVersion != 1) throw AgentCoreErrors.Conflict("This environment uses the earlier workspace contract. Reset it and reverify its profile before using commands.");
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
        if (!await TryTerminateRemoteAsync(lease, ct)) return false;
        return TryDeleteStaging(lease);
    }
    private bool TryDeleteStaging(SandboxLease lease)
    {
        try { if (lease.MountContractVersion == 1) Mounts.Delete(lease.Generation); return true; }
        catch
        {
            logger?.LogWarning("Sandbox staging cleanup remains pending. SessionId={SessionId} Generation={Generation} Failure=localStaging", lease.SessionId, lease.Generation);
            return false;
        }
    }
    private async Task<bool> TryTerminateRemoteAsync(SandboxLease lease, CancellationToken ct)
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
    public async ValueTask<SandboxEnvironmentInfo?> InspectAsync(Guid trustedSessionId, CancellationToken ct = default)
    {
        var lease = await store.GetLeaseAsync(trustedSessionId, ct);
        return lease is null ? null : new(lease.SessionId, lease.Generation, lease.State.ToString(), lease.Profile.Id, lease.Profile.Revision, lease.ExpiresAt, lease.ActiveExecutionId, lease.DiagnosticId, lease.LastExecutionId, lease.SafeMessage,
            lease.CreatedAt, lease.LastActivityAt ?? lease.InitializedAt ?? lease.CreatedAt, lease.CreatedAt.AddSeconds(lease.Profile.MaxLifetimeSeconds),
            lease.CleanupReason, lease.State is SandboxLeaseState.Terminating or SandboxLeaseState.NeedsReconciliation, lease.CleanupPasses,
            lease.LastCleanupAttemptAt, lease.Initialization, lease.Profile.Name, lease.Profile.ImageReference, lease.CleanupReason == "reset" ? null : lease.SelectedProfileId ?? lease.Profile.Id, lease.TransitionProfileId, lease.TransitionPhase, lease.FailureCategory, lease.FailurePhase, lease.AdmissionRunId);
    }
    public ValueTask StopSessionAsync(Guid trustedSessionId, CancellationToken ct = default, Guid? expectedGeneration = null)
        => StopSessionAsync(trustedSessionId, ct, expectedGeneration, null, false);
    public async ValueTask StopSessionAsync(Guid trustedSessionId, CancellationToken ct, Guid? expectedGeneration, Guid? expectedExecutionId, bool checkExecutionIdentity)
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
                if (expectedGeneration is not null && lease.CleanupReason != "reset")
                    await SaveAsync(lease with { CleanupReason = "reset", SelectedProfileId = null, TransitionProfileId = null, TransitionProfileRevision = null, TransitionPhase = null }, ct);
                return;
            }
            var metadata = await sessions.LoadMetadataAsync(trustedSessionId, ct);
            var reason = expectedGeneration is not null ? "reset" : metadata is null || metadata.DurablyDeletedAt is not null ? "sessionDeleted"
                : metadata.ArchivedAt is not null ? "sessionArchived" : metadata.Status is SessionStatus.Ending or SessionStatus.Ended ? "sessionEnded"
                : metadata.LifecycleStatus != SessionLifecycleStatus.Active ? "sessionDeactivated" : "sessionLifecycle";
            lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminating, CleanupReason = reason, SelectedProfileId = reason == "reset" ? null : lease.SelectedProfileId,
                TransitionProfileId = reason == "reset" ? null : lease.TransitionProfileId, TransitionProfileRevision = reason == "reset" ? null : lease.TransitionProfileRevision, TransitionPhase = reason == "reset" ? null : lease.TransitionPhase, CleanupPasses = lease.CleanupPasses + 1, LastCleanupAttemptAt = time.GetUtcNow() }, ct);
            var pending = lease.ActiveExecutionId is { } id ? await store.GetExecutionAsync(trustedSessionId, id, ct) : null;
            if (pending is null && lease.LastExecutionId is { } lastId
                && await store.GetExecutionAsync(trustedSessionId, lastId, ct) is { } last
                && (HasPendingFinalization(last) || last.WorkspaceSyncStatus is "pending" or "committing" or "unknown"))
                pending = last; // An interrupted lease update may have cleared the active pointer.
            if (pending is not null)
            {
                // Persist the discard intent before termination. Recovery must not import
                // a completed-but-unpublished command after owner reset or Session end.
                pending = pending with { FailureCategory = pending.FailureCategory ?? "cancellation",
                    CancellationReason = reason == "reset" ? "ownerReset" : "sessionLifecycle" };
                await SaveReceiptAsync(pending, false, ct);
            }
            if (!await TryTerminateRemoteAsync(lease, ct)) throw AgentCoreErrors.Persistence("Sandbox cleanup is pending; its durable receipt will be reconciled.");
            if (pending is not null)
            {
                if (pending.State is SandboxExecutionState.Dispatching or SandboxExecutionState.Running)
                    pending = pending with { State = SandboxExecutionState.Canceled, FinishedAt = time.GetUtcNow() };
                await SettleTerminatedExecutionAsync(lease, pending, ct);
                if ((await store.GetLeaseAsync(trustedSessionId, ct))?.State != SandboxLeaseState.Terminated)
                    throw AgentCoreErrors.Persistence("Sandbox file outcome or staging cleanup remains unresolved; retained evidence must be reconciled.");
            }
            else
            {
                if (!TryDeleteStaging(lease)) throw AgentCoreErrors.Persistence("Sandbox staging cleanup is pending.");
                await SaveAsync(lease with { State = SandboxLeaseState.Terminated, ActiveExecutionId = null }, ct);
            }
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
                var finalizingId = lease.ActiveExecutionId ?? lease.LastExecutionId;
                var finalizing = finalizingId is { } fid ? await store.GetExecutionAsync(lease.SessionId, fid, ct) : null;
                if (finalizing is not null && HasPendingFinalization(finalizing))
                {
                    await ReconcileFinalizationAsync(lease, finalizing, ct);
                    continue;
                }
                if (lease.State == SandboxLeaseState.NeedsResync && lease.LastExecutionId is { } lastId
                    && await store.GetExecutionAsync(lease.SessionId, lastId, ct) is { } last)
                {
                    if (await TryTerminateRemoteAsync(lease, ct)) await SettleTerminatedExecutionAsync(lease, last, ct);
                    continue;
                }
                var session = await sessions.LoadMetadataAsync(lease.SessionId, ct);
                var source = await Images.InspectAsync(lease.Profile, ct, requireVerification:false);
                var revoked = source.Status != "SourceAuthorized" || source.CredentialRevision != lease.Profile.RegistryCredentialRevision
                    || source.SecretRevision != lease.Profile.RegistrySecretRevision || !options.Enabled || (await store.GetProfileAsync(lease.Profile.Id, ct: ct))?.Enabled != true
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
                    else if (await TryTerminateRemoteAsync(lease, ct)) await FinalizeCleanupAsync(lease, ct);
                }
                else if (lease.ActiveExecutionId is { } executionId)
                {
                    var execution = await store.GetExecutionAsync(lease.SessionId, executionId, ct);
                    if (execution is { WorkspaceSyncStatus: "committing" })
                    {
                        var killed = await TryTerminateRemoteAsync(lease, ct);
                        if (killed) await SettleTerminatedExecutionAsync(lease, execution, ct);
                        else await SaveAsync(lease with { State = SandboxLeaseState.NeedsReconciliation }, ct);
                        continue;
                    }
                    if (execution is { Background: false, State: SandboxExecutionState.Dispatching or SandboxExecutionState.Running } && session?.ProfileId is { } ownerProfile)
                    {
                        var run = await runs.GetAsync(new(lease.AgentInstanceId,ownerProfile),execution.AgentRunId,ct);
                        if (run?.Claim?.Generation != execution.ClaimGeneration || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow() || run.CancellationRequested || (execution.DeadlineAt ?? execution.StartedAt.AddSeconds(lease.Profile.MaxCommandSeconds)) <= time.GetUtcNow())
                        {
                            var killed = await TryTerminateRemoteAsync(lease,ct);
                            var retained = execution with { State = run?.CancellationRequested == true ? SandboxExecutionState.Canceled : SandboxExecutionState.Indeterminate,
                                FailureCategory = run?.CancellationRequested == true ? "cancellation" : "ownerLost", CancellationReason = run?.CancellationRequested == true ? "runCancellation" : null };
                            if (killed) await SettleTerminatedExecutionAsync(lease, retained, ct);
                            else { await SaveReceiptAsync(retained, false, ct); await SaveAsync(lease with { State = SandboxLeaseState.NeedsReconciliation },ct); }
                            continue;
                        }
                    }
                    if (execution is { State: SandboxExecutionState.Completed or SandboxExecutionState.Canceled or SandboxExecutionState.Indeterminate })
                    {
                        // The receipt is saved before clearing the lease. Recover
                        // an interrupted lease update without reimporting any tree.
                        var status = await CommitStatusAsync(execution, ct);
                        await SaveReceiptAsync(execution with { WorkspaceSyncStatus = status }, false, ct);
                        if (execution.TerminationConfirmed) await SettleTerminatedExecutionAsync(lease, execution, ct);
                        else await SaveAsync(lease with { ActiveExecutionId = null, State = status == "committed" && execution.State == SandboxExecutionState.Completed ? SandboxLeaseState.Ready : SandboxLeaseState.NeedsResync }, ct);
                    }
                    else if (execution is { Background: true })
                    {
                        var ownerRun = session?.ProfileId is { } profileId ? await runs.GetAsync(new(lease.AgentInstanceId, profileId), execution.AgentRunId, ct) : null;
                        if (ownerRun is null || ownerRun.CancellationRequested || ownerRun.Status is AgentRunStatus.Cancelled or AgentRunStatus.Failed
                            || (execution.DeadlineAt ?? execution.StartedAt.AddSeconds(lease.Profile.MaxCommandSeconds)) <= time.GetUtcNow())
                        {
                            if (await TryTerminateRemoteAsync(lease, ct)) await FinalizeCleanupAsync(lease, ct);
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
        var id = lease.ActiveExecutionId ?? lease.LastExecutionId;
        if (id is { } executionId && await store.GetExecutionAsync(lease.SessionId, executionId, ct) is { } execution)
        {
            if (execution.State is SandboxExecutionState.Dispatching or SandboxExecutionState.Running && lease.CleanupReason is "reset" or "cancelled" or "sessionLifecycle" or "sessionDeleted" or "sessionArchived" or "sessionEnded")
                execution = execution with { State = SandboxExecutionState.Canceled, FailureCategory = "cancellation", CancellationReason = lease.CleanupReason == "reset" ? "ownerReset" : "sessionLifecycle" };
            await SettleTerminatedExecutionAsync(lease, execution, ct);
            return;
        }
        if (!TryDeleteStaging(lease)) { await SaveAsync(lease with { State = SandboxLeaseState.NeedsReconciliation }, ct); return; }
        await SaveAsync(lease with { State = SandboxLeaseState.Terminated, ActiveExecutionId = null }, ct);
    }
    private static SandboxCommandResult Result(SandboxExecution e) => new(e.Id, e.State.ToString(), e.ExitCode, e.Stdout, e.Stderr, e.Truncated,
        e.DurationMs ?? (e.FinishedAt is { } end ? Math.Max(0, (long)(end - e.StartedAt).TotalMilliseconds) : 0), e.WorkspaceSyncStatus, e.DiagnosticId, e.Background, e.CommandText, e.ProfileId, e.ProfileRevision, e.Generation, e.Phase, e.FailureCategory, e.CancellationReason, e.TerminationConfirmed, GenerationRecycled: e.GenerationRecycled, RemoteTerminationConfirmed: e.RemoteTerminationConfirmed);
    private sealed class BoundedText(int maxBytes)
    {
        private readonly StringBuilder _text = new(); private int _bytes;
        public bool Truncated { get; private set; } public string Text => _text.ToString();
        public void Append(string value) { foreach (var rune in value.EnumerateRunes()) { if (_bytes + rune.Utf8SequenceLength > maxBytes) { Truncated = true; return; } _text.Append(rune.ToString()); _bytes += rune.Utf8SequenceLength; } }
    }
}
