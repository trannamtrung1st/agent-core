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
    ILogger<OpenSandboxService>? logger = null, SandboxImageAuthority? imageAuthority = null, IAgentRunAuthority? runAuthority = null, IAgentDefinitionStore? definitions = null, ITenantSandboxVerificationStore? tenantVerifications = null,
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
    private async ValueTask ValidateAccessAsync(SandboxAccess access, CancellationToken ct, bool execution = true)
    {
        var session = await sessions.LoadMetadataAsync(access.SessionId, ct);
        if (session is null || session.AgentInstanceId != access.AgentInstanceId || session.ProfileId is null || session.DurablyDeletedAt is not null
            || session.ArchivedAt is not null || session.LifecycleStatus != SessionLifecycleStatus.Active || session.Status is SessionStatus.Ended or SessionStatus.Ending)
            throw AgentCoreErrors.NotFound("Active sandbox Session was not found.");
        var run = await runs.GetAsync(new(access.AgentInstanceId, session.ProfileId.Value), access.AgentRunId, ct);
        if (instances is null || (await instances.FindAsync(access.AgentInstanceId,ct))?.Lifecycle != AgentInstanceLifecycle.Active)
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
        if (access.Profile.TenantId is not null) throw new AgentCore.Application.Security.AccessDeniedException("tenant_scope_required");
        SandboxCommands.Validate(command);
        var gate = Gate(access.SessionId);
        if (!await gate.WaitAsync(0, ct))
        {
            await ValidateAccessAsync(access, ct, execution: false);
            var busy = await store.GetLeaseAsync(access.SessionId, ct);
            var result = busy is null ? new SandboxCommandResult(Guid.Empty, "Busy", null, "", "", false, 0, false, Error: "recovery_required", EnvironmentState: "Busy")
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
            if (observed is not null && (observed.State is SandboxLeaseState.NeedsReconciliation or SandboxLeaseState.Terminating or SandboxLeaseState.Failed or SandboxLeaseState.Provisioning
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
            try { MountResolver.ValidateConfiguration(); }
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
            await CheckMountedAuthorityAsync(lease, ct);
            ValidateCwd(lease, command.Cwd);
            lease = await ReserveExecutionLifetimeAsync(sdk, lease, timeout, ct);
            var receipt = new SandboxExecution(Guid.CreateVersion7(), access.SessionId, access.AgentInstanceId, lease.Generation,
                access.AgentRunId, access.ClaimGeneration, access.ToolCallId, hash, SandboxExecutionState.Dispatching, time.GetUtcNow(), Background: command.Background, ProfileId: lease.Profile.Id, ProfileRevision: lease.Profile.Revision, Phase: "dispatch",
                DeadlineAt: time.GetUtcNow().AddSeconds(timeout), FilesystemEffectsMayHaveOccurred: true,
                CommandText: command.Command ?? JsonSerializer.Serialize(command.Argv, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            receipt = await SaveReceiptAsync(receipt, true, ct);
            lease = await SaveAsync(lease with { State = SandboxLeaseState.Executing, ActiveExecutionId = receipt.Id, LastExecutionId = receipt.Id, LastActivityAt = time.GetUtcNow(), AdmissionRunId = access.AgentRunId, AdmissionToolCallId = access.ToolCallId }, ct);
            return await ExecuteReservedCommandAsync(sdk, lease, receipt, command, access.Definition,
                token => ValidateAccessAsync(access, token), ct);
        }
        finally { gate.Release(); }
    }

    private async ValueTask<SandboxLease> EnsureLockedAsync(SandboxAccess access, CancellationToken ct)
    {
        await workspace.EnsureAsync(access.SessionId, access.Definition, ct);
        var mounted = MountResolver.Resolve(access.AgentInstanceId, access.SessionId, await EffectiveHomeAsync(access, ct), create: true, ct: ct);
        var lease = await store.GetLeaseAsync(access.SessionId, ct);
        if (lease is { State: SandboxLeaseState.Terminated, Initialization.State: "Running" or "Failed" } && lease.CleanupReason != "reset")
            throw AgentCoreErrors.Conflict("Initialization failed or its outcome is uncertain. Inspect its retained evidence and explicitly reset before retrying.");
        if (lease is not null && lease.AgentInstanceId != access.AgentInstanceId) throw AgentCoreErrors.NotFound("Sandbox was not found.");
        if (lease is not null && lease.State != SandboxLeaseState.Terminated)
        {
            if (lease.MountAuthority != mounted.Authority with { TranslatedOwnership = lease.MountAuthority?.TranslatedOwnership ?? false } || lease.Profile.Id != access.Profile.Id || lease.Profile.Revision != access.Profile.Revision
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
                    throw AgentCoreErrors.Conflict("Switching requires a quiescent generation with resolved process cleanup.");
                await using (var connected = await ConnectAsync(lease, ct)) await ConfirmQuiescentAsync(connected, lease, ct);
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
                        lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminated }, ct);
                    }
                }
                else return lease;
            }
        }
        var imageAuth = await Images.ResolveAuthAsync(access.Profile, ct, requireVerification: true);
        var now = time.GetUtcNow();
        var initialTtl = Math.Min(access.Profile.MaxLifetimeSeconds,
            Math.Max(access.Profile.IdleTtlSeconds, 45 + access.Profile.MaxCommandSeconds + ProcessCleanupGraceSeconds));
        if ((!string.IsNullOrWhiteSpace(access.Profile.InitializationScript) || !string.IsNullOrWhiteSpace(access.Profile.InstanceInitializationScript)) && initialTtl < 45 + access.Profile.MaxCommandSeconds + ProcessCleanupGraceSeconds)
            throw AgentCoreErrors.Conflict("Maximum sandbox lifetime cannot cover initialization. Increase it within the host ceiling.");
        lease = new(access.SessionId, access.AgentInstanceId, Guid.CreateVersion7(), (lease?.Revision ?? 0) + 1, access.Profile,
            SandboxLeaseState.Provisioning, null, now, now.AddSeconds(initialTtl),
            Initialization: new("Pending", access.Profile.Id, access.Profile.Revision, access.Profile.InstanceRevision), MountAuthority: mounted.Authority,
            SelectedProfileId: access.Profile.Id, TransitionPhase: "provisioning", AdmissionRunId: access.AgentRunId, AdmissionToolCallId: access.ToolCallId);
        await store.SaveLeaseAsync(lease, lease.Revision - 1, ct);
        SdkSandbox? sdk = null;
        var phase = "provisioning";
        try
        {
            if (!OciImageReference.Parse(lease.Profile.ImageReference).IsLocal) await RegistryAuthorizationProbe.CheckAsync(options, OciImageReference.Parse(lease.Profile.ImageReference), imageAuth, ct);
            await ValidateAccessAsync(access, ct);
            imageAuth = await Images.ResolveAuthAsync(access.Profile,ct,requireVerification:true);
            phase = "provisioning";
            sdk = await SdkSandbox.CreateAsync(new SandboxCreateOptions {
                ConnectionConfig = Connection(), Image = lease.Profile.ImageReference, ImageAuth = imageAuth, TimeoutSeconds = initialTtl, ReadyTimeoutSeconds = 45,
                Resource = new Dictionary<string,string> { ["cpu"] = lease.Profile.Cpu.ToString(CultureInfo.InvariantCulture), ["memory"] = lease.Profile.MemoryMiB + "Mi" },
                Metadata = new Dictionary<string,string> { ["agent-core-generation"] = lease.Generation.ToString("N"), ["agent-core-filesystem-contract"] = SandboxProfileVerification.CurrentContractVersion.ToString(CultureInfo.InvariantCulture) },
                Volumes = mounted.Volumes,
                NetworkPolicy = new NetworkPolicy { DefaultAction = NetworkRuleAction.Deny, Egress = [] }
            }, ct);
            imageAuth = null;
            lease = await SaveAsync(lease with { ProviderId = sdk.Id }, ct);
            phase = "mountValidation";
            var translated = await AttestMountsAsync(sdk.Id, mounted, lease.Profile.ImageReference, "agent-core-generation", lease.Generation.ToString("N"), ct);
            var authority = await CheckMountedReadinessAsync(sdk, mounted, translated, ct);
            lease = await SaveAsync(lease with { MountAuthority = authority }, ct);
            phase = "processBaseline";
            lease = await SaveAsync(lease with { BaselineProcesses = await ProcessIdentitiesAsync(sdk, ct) }, ct);
            if (!string.IsNullOrWhiteSpace(lease.Profile.InitializationScript) || !string.IsNullOrWhiteSpace(lease.Profile.InstanceInitializationScript))
            {
                phase = "initialization";
                lease = await ReserveExecutionLifetimeAsync(sdk, lease, lease.Profile.MaxCommandSeconds, ct);
                lease = await InitializeLockedAsync(sdk, lease, ct);
            }
            else lease = await SaveAsync(lease with { Initialization = new("Succeeded", lease.Profile.Id, lease.Profile.Revision, lease.Profile.InstanceRevision,
                time.GetUtcNow(), time.GetUtcNow(), 0) }, ct);
            return await SaveAsync(lease with { State = SandboxLeaseState.Ready, TransitionPhase = null, InitializedAt = time.GetUtcNow(), LastActivityAt = time.GetUtcNow(), ExpiresAt = await RenewIdleAsync(sdk, lease, ct) }, ct);
        }
        catch (Exception caught)
        {
            var error = caught is SandboxApiException { Error.Code: "MountIncompatible" }
                ? CanonicalSandboxMountResolver.Incompatible("Automatic preparation could not establish safe filesystem access. The deployment mapping or execution configuration is unsupported.") : caught;
            if (error is AgentCoreException { Code: "MountIncompatible" }) phase = "mountValidation";
            // Retain a safe cause even when orphan cleanup later reports pending.
            // SDK messages/bodies, provider IDs and credentials are never logged.
            var failure = error is SandboxApiException ? "http" : error is OperationCanceledException ? "canceled"
                : error is HttpRequestException ? "transport" : "setup";
            var diagnosticId = Guid.CreateVersion7().ToString("D");
            logger?.LogWarning("Sandbox provisioning failed. DiagnosticId={DiagnosticId} SessionId={SessionId} Generation={Generation} ProfileId={ProfileId} Phase={Phase} Failure={Failure} ErrorType={ErrorType} StatusCode={StatusCode} ProviderLinked={ProviderLinked}",
                diagnosticId, lease.SessionId, lease.Generation, lease.Profile.Id, phase, failure, error.GetType().Name, (error as SandboxApiException)?.StatusCode, lease.ProviderId is not null);
            lease = await store.GetLeaseAsync(access.SessionId, CancellationToken.None) ?? lease;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var terminated = lease.ProviderId is not null && await TryTerminateAsync(lease, cleanup.Token);
            await SaveAsync(lease with { State = terminated ? SandboxLeaseState.Failed : SandboxLeaseState.NeedsReconciliation, DiagnosticId = diagnosticId,
                CleanupReason = phase == "initialization" ? "initializationFailed" : "setupFailed", FailurePhase = phase,
                FailureCategory = error is AgentCoreException { Code: "WorkspaceQuotaExceeded" } ? "storageLimit" : error is OperationCanceledException ? "cancellation" : error is AgentCoreException { Code: "Forbidden" } ? "sourceAuthorizationChanged"
                    : phase.StartsWith("mount", StringComparison.Ordinal) ? "mountFailure" : "provisioningFailure",
                SafeMessage = error is AgentCoreException { Code: "MountIncompatible" } ? "Sandbox filesystem is incompatible with this Docker environment. Automatic preparation could not establish safe access. Reverify after repairing the trusted deployment; an identical reset will not help." : phase == "initialization" ? "Environment initialization failed. Correct and verify the profile script, then reset before retrying." : error is AgentCoreException sourceError ? sourceError.Message + " Inspect the saved source, then reset before retrying." : "Environment setup failed. Check private server readiness and the pinned image, then reset before retrying." }, CancellationToken.None);
            if (error is AgentCoreException { Code: "MountIncompatible" })
                throw CanonicalSandboxMountResolver.Incompatible(lease.SafeMessage ?? "Sandbox filesystem compatibility failed. Inspect readiness and reverify after repairing the deployment.");
            throw AgentCoreErrors.Persistence("Sandbox provisioning failed. Inspect readiness and reset before retrying.");
        }
        finally { if (sdk is not null) await sdk.DisposeAsync(); }
    }

    // Reserve command deadline plus bounded process retirement time, while
    // retaining the immutable profile's hard maximum lifetime.
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
        && (lease.TenantId is null || metadata.GetValueOrDefault("agent-core-tenant") == lease.TenantId.Value.ToString("N")
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
    private Task<bool> TryTerminateAsync(SandboxLease lease, CancellationToken ct) => TryTerminateRemoteAsync(lease, ct);
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
                if (lease.TenantId is not null)
                {
                    try
                    {
                        var info = await manager.GetSandboxInfoAsync(lease.ProviderId, request.Token);
                        if (!MatchesGeneration(lease, info.Metadata)) { failure = "ownership"; break; }
                    }
                    catch (SandboxApiException e) when (e.StatusCode == 404) { return true; }
                }
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
        var state = lease?.State.ToString();
        if (lease is not null && lease.State != SandboxLeaseState.Terminated)
        {
            try { await CheckMountedAuthorityAsync(lease, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { state = "EnforcementPending"; }
        }
        return lease is null ? null : new(lease.SessionId, lease.Generation, state!, lease.Profile.Id, lease.Profile.Revision, lease.ExpiresAt, lease.ActiveExecutionId, lease.DiagnosticId, lease.LastExecutionId, state == "EnforcementPending" ? "Saved authority no longer matches this live mount or could not be verified. Retirement is pending; writes may continue until termination is confirmed." : lease.SafeMessage,
            lease.CreatedAt, lease.LastActivityAt ?? lease.InitializedAt ?? lease.CreatedAt, lease.CreatedAt.AddSeconds(lease.Profile.MaxLifetimeSeconds),
            lease.CleanupReason, state == "EnforcementPending" || lease.State is SandboxLeaseState.Terminating or SandboxLeaseState.NeedsReconciliation, lease.CleanupPasses,
            lease.LastCleanupAttemptAt, lease.Initialization, lease.Profile.Name, lease.Profile.ImageReference, lease.CleanupReason == "reset" ? null : lease.SelectedProfileId ?? lease.Profile.Id, lease.TransitionProfileId, lease.TransitionPhase, lease.FailureCategory, lease.FailurePhase, lease.AdmissionRunId, lease.MountAuthority?.HomeAccess.ToString(), "sampled");
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
                var executionId = lease.ActiveExecutionId ?? lease.LastExecutionId;
                var execution = executionId is { } id ? await store.GetExecutionAsync(lease.SessionId, id, ct) : null;
                if (execution is not null && HasPendingFinalization(execution))
                { await ReconcileFinalizationAsync(lease, execution, ct); continue; }
                var session = await sessions.LoadMetadataAsync(lease.SessionId, ct);
                var idleDetached = session?.LifecycleStatus == SessionLifecycleStatus.Paused && session.LifecycleReason == "disconnected"
                    && lease.State == SandboxLeaseState.Ready && lease.ActiveExecutionId is null;
                var retire = session is null || session.DurablyDeletedAt is not null || session.ArchivedAt is not null
                    || session.LifecycleStatus != SessionLifecycleStatus.Active && !idleDetached
                    || session.Status is SessionStatus.Ended or SessionStatus.Ending || lease.ExpiresAt <= time.GetUtcNow()
                    || lease.State is SandboxLeaseState.Provisioning or SandboxLeaseState.Terminating or SandboxLeaseState.NeedsReconciliation or SandboxLeaseState.Failed;
                if (!retire)
                {
                    try { await CheckMountedAuthorityAsync(lease, ct); }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    { retire = true; lease = lease with { CleanupReason = "policyChanged" }; }
                    if (!retire)
                    {
                        try { CanonicalSandboxMountResolver.CheckUsage(ResolveMounts(lease), lease.Profile.StorageBytes, ct); }
                        catch (Exception) when (!ct.IsCancellationRequested) { retire = true; }
                    }
                }
                if (!retire && execution is { State: SandboxExecutionState.Running or SandboxExecutionState.Dispatching })
                {
                    var run = session?.ProfileId is { } profile ? await runs.GetAsync(new(lease.AgentInstanceId, profile), execution.AgentRunId, ct) : null;
                    retire = run is null || run.CancellationRequested || run.Status is AgentRunStatus.Cancelled or AgentRunStatus.Failed
                        || (execution.DeadlineAt ?? execution.StartedAt.AddSeconds(lease.Profile.MaxCommandSeconds)) <= time.GetUtcNow()
                        || !execution.Background && (run.Claim?.Generation != execution.ClaimGeneration || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow());
                    if (!retire && execution.Background) { await RefreshProcessLockedAsync(lease, execution, false, ct); continue; }
                }
                if (!retire) continue;
                lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminating, CleanupReason = lease.CleanupReason ?? "authorityOrLifetime",
                    CleanupPasses = lease.CleanupPasses + 1, LastCleanupAttemptAt = time.GetUtcNow() }, ct);
                if (lease.ProviderId is null)
                {
                    await using var manager = SandboxManager.Create(new SandboxManagerOptions { ConnectionConfig = Connection() });
                    var page = await manager.ListSandboxInfosAsync(new SandboxFilter { Metadata = new Dictionary<string,string> { ["agent-core-generation"] = lease.Generation.ToString("N") }, PageSize = 100 }, ct);
                    foreach (var remote in page.Items) await manager.KillSandboxAsync(remote.Id, ct);
                    if (page.Items.Count < 100) await FinalizeCleanupAsync(lease, ct);
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
