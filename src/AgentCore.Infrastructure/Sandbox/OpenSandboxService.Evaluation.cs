using System.Globalization;
using System.Text;
using AgentCore.Application.Evaluations;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Evaluations;
using AgentCore.Infrastructure.Workspaces;
using OpenSandbox;
using OpenSandbox.Models;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    internal Action<string>? EvaluationCheckpoint { get; set; }
    public string EvaluationHostScope => HostScope;
    public bool EvaluationEnabled => options.Enabled;
    private ITenantEvaluationStore EvaluationStore => evaluations ?? throw new AccessDeniedException("sandbox_evaluation_unavailable");
    private EvaluationSandboxAuthority EvaluationAuthority => evaluationAuthority ?? throw new AccessDeniedException("sandbox_evaluation_unavailable");
    private SandboxLease EvaluationLease(EvaluationSandboxExecution value) => new(value.ExecutionId, value.InstanceId, value.ExecutionId, 1,
        value.Profile with { StorageBytes = Math.Min(value.Profile.StorageBytes, 16L * 1024 * 1024) }, SandboxLeaseState.Provisioning, value.ProviderId,
        value.CreatedAt, value.DispatchExpiresAt ?? value.ExpiresAt, TenantId: value.TenantId, ExecutorPrincipalId: value.ExecutorId, ProviderHostScope: value.HostScope);

    public async ValueTask ExecuteEvaluationAsync(Guid tenant, Guid executionId, Guid dispatchClaim, CancellationToken ct = default)
    {
        var value = await EvaluationStore.FindSandboxEvaluationAsync(tenant, executionId, ct) ?? throw new AccessDeniedException("sandbox_evaluation_unavailable");
        if (!options.Enabled || value.HostScope != EvaluationHostScope || value.Phase != EvaluationSandboxPhase.Reserved)
            throw new AccessDeniedException("sandbox_evaluation_dispatch_unavailable");
        var dispatchAuthority = await EvaluationAuthority.RequireAsync(value, ct, approved: true, dispatchClaim: dispatchClaim);
        var dispatched = value with { Phase = EvaluationSandboxPhase.Dispatching, Revision = value.Revision + 1, CleanupPending = true };
        if (!await EvaluationStore.TrySaveSandboxEvaluationAsync(dispatched, value.Revision, ct, dispatchAuthority.Tenant.Revision)) throw new SecurityConflictException("sandbox_evaluation_already_dispatched");
        value = (await EvaluationStore.FindSandboxEvaluationAsync(tenant, executionId, ct))!;
        // This durable CAS owns one SDK create/command path. Re-entering Dispatching cannot replay it.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, (value.DispatchExpiresAt!.Value - time.GetUtcNow()).TotalSeconds)));
        using var watcherStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        SdkSandbox? sdk = null; CanonicalSandboxMountResolver.Mounts? mounted = null;
        var stdout = new BoundedText(4096); var stderr = new BoundedText(4096);
        int? exitCode = null; var known = false; string reason = "sandbox_evaluation_inconclusive";
        async ValueTask Require(CancellationToken token)
        {
            var current = await EvaluationStore.FindSandboxEvaluationAsync(tenant, executionId, token) ?? throw new AccessDeniedException("sandbox_evaluation_unavailable");
            await EvaluationAuthority.RequireAsync(current, token, approved: true, dispatchClaim: dispatchClaim);
        }
        string? authorityFailure = null;
        var watch = Watch();
        async Task Watch()
        {
            try { while (true) { await Task.Delay(TimeSpan.FromMilliseconds(250), time, watcherStop.Token); await Require(watcherStop.Token); } }
            catch (OperationCanceledException) when (watcherStop.IsCancellationRequested) { }
            catch (AccessDeniedException denied) { authorityFailure = denied.ReasonCode; await deadline.CancelAsync(); }
            catch { authorityFailure = "sandbox_evaluation_authority_unavailable"; await deadline.CancelAsync(); }
        }
        try
        {
            await RequireTenantRetirementCapabilityAsync(deadline.Token, evaluation: true);
            await Require(deadline.Token);
            mounted = MountResolver.Evaluation(executionId, deadline.Token);
            await mounted.Home!.WriteAsync("/home/input.json", Encoding.UTF8.GetBytes(value.InputJson!), 65536, 65536, deadline.Token, Require);
            await Require(deadline.Token);
            var auth = await Images.ResolveAuthAsync(value.Profile, deadline.Token, requireVerification: true, Require);
            var metadata = new Dictionary<string, string> { ["agent-core-tenant"] = tenant.ToString("N"), ["agent-core-session"] = executionId.ToString("N"),
                ["agent-core-executor"] = value.ExecutorId.ToString("N"), ["agent-core-generation"] = executionId.ToString("N"),
                ["agent-core-evaluation-attempt"] = value.AttemptId.ToString("N"), ["agent-core-filesystem-contract"] = "5" };
            await Require(deadline.Token);
            sdk = await SdkSandbox.CreateAsync(new SandboxCreateOptions {
                ConnectionConfig = Connection(), Image = value.Profile.ImageReference, ImageAuth = auth, TimeoutSeconds = 120, ReadyTimeoutSeconds = 45,
                Resource = new Dictionary<string, string> { ["cpu"] = Math.Min(value.Profile.Cpu, 1).ToString(CultureInfo.InvariantCulture), ["memory"] = Math.Min(value.Profile.MemoryMiB, 512) + "Mi" },
                Metadata = metadata, Volumes = mounted.Volumes, NetworkPolicy = new NetworkPolicy { DefaultAction = NetworkRuleAction.Deny, Egress = [] }
            }, deadline.Token);
            auth = null;
            // Persist returned technical identity even if source authority changed while create was in flight.
            value = (await EvaluationStore.FindSandboxEvaluationAsync(tenant, executionId, CancellationToken.None))!;
            var identified = value with { Revision = value.Revision + 1, ProviderId = sdk.Id };
            if (!await EvaluationStore.TrySaveSandboxEvaluationAsync(identified, value.Revision, CancellationToken.None)) throw new SecurityConflictException("stale_revision");
            value = identified;
            await Require(deadline.Token);
            if (!MatchesGeneration(EvaluationLease(value), (await sdk.GetInfoAsync(deadline.Token)).Metadata)) throw new AccessDeniedException("sandbox_generation_unavailable");
            var translated = await AttestMountsAsync(sdk.Id, mounted, value.Profile.ImageReference, "agent-core-generation", executionId.ToString("N"), deadline.Token);
            var authority = await CheckMountedReadinessAsync(sdk, mounted, translated, deadline.Token);
            await Require(deadline.Token);
            var command = TenantApprovalRules.SandboxCommand(value.Approval.Operation.SandboxCommand!);
            using var commandDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); commandDeadline.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds!.Value));
            var result = await GuardCommandStorageAsync(EvaluationLease(value), token => sdk.Commands.RunAsync(SandboxCommandPermissions.Shell(command.Command!),
                CommandOptions(authority, "/working", command.TimeoutSeconds.Value), new ExecutionHandlers { SkipAccumulation = true, OnInit = _ => { EvaluationCheckpoint?.Invoke("commandStarted"); return Task.CompletedTask; },
                    OnStdout = m => { stdout.Append(m.Text); return Task.CompletedTask; }, OnStderr = m => { stderr.Append(m.Text); return Task.CompletedTask; } }, token), commandDeadline.Token, mounted);
            exitCode = result.ExitCode;
            known = result.Error?.Name?.Contains("Timeout", StringComparison.OrdinalIgnoreCase) != true && exitCode is >= 0 and <= 255;
            reason = known ? exitCode == 0 ? "sandbox_assertion_passed" : "sandbox_assertion_failed" : "sandbox_evaluation_result_unknown";
            await Require(deadline.Token);
            EvaluationCheckpoint?.Invoke("commandCompleted");
        }
        catch (OperationCanceledException) { known = false; reason = ct.IsCancellationRequested ? "sandbox_evaluation_cancelled" : authorityFailure ?? "sandbox_evaluation_timeout_or_revoked"; }
        catch (AccessDeniedException denied) { known = false; reason = denied.ReasonCode == "tenant_sandbox_recovery_unavailable" ? "sandbox_evaluation_host_upgrade_required" : denied.ReasonCode; }
        catch (Exception error) when (error is not FinalizationCrash) { known = false; reason = "sandbox_evaluation_authority_or_provider_unavailable"; }
        finally
        {
            await watcherStop.CancelAsync(); await watch;
            if (sdk is not null) { try { await sdk.DisposeAsync(); } catch { /* Retirement still owns cleanup. */ } }
        }
        value = (await EvaluationStore.FindSandboxEvaluationAsync(tenant, executionId, CancellationToken.None))!;
        using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var clean = false;
        try { clean = await CleanupEvaluationAsync(value, cleanupBudget.Token); } catch { /* The durable owner retains cleanup obligations. */ }
        if (value.CancellationRequestedAt is not null) { known = false; reason = "sandbox_evaluation_cancelled"; }
        long? securityRevision = null;
        if (known && clean)
        {
            try { securityRevision = (await EvaluationAuthority.RequireAsync(value, CancellationToken.None, approved: true, dispatchClaim: dispatchClaim)).Tenant.Revision; }
            catch { known = false; reason = "sandbox_evaluation_authority_changed"; }
        }
        if (!clean) { known = false; reason = "sandbox_evaluation_cleanup_pending"; }
        var settled = value with { Revision = value.Revision + 1, Phase = EvaluationSandboxPhase.Settled, InputJson = null,
            Outcome = known ? exitCode == 0 ? EvaluationOutcome.Passed : EvaluationOutcome.Failed : EvaluationOutcome.Inconclusive, ReasonCode = reason,
            ExitCode = exitCode, DurationMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            OutputHash = EvaluationRules.Hash(new { stdout = stdout.Text, stderr = stderr.Text, truncated = stdout.Truncated || stderr.Truncated }),
            CleanupPending = !clean, TerminationConfirmed = clean, RetryCleanupAt = clean ? null : time.GetUtcNow().AddSeconds(30) };
        if (!await EvaluationStore.TrySaveSandboxEvaluationAsync(settled, value.Revision, CancellationToken.None, securityRevision))
        {
            var latest = await EvaluationStore.FindSandboxEvaluationAsync(tenant, executionId, CancellationToken.None);
            if (latest is { Phase: EvaluationSandboxPhase.Dispatching } && latest.DispatchClaim == dispatchClaim)
                _ = await EvaluationStore.TrySaveSandboxEvaluationAsync(settled with { Revision = latest.Revision + 1,
                    CancellationRequestedAt = latest.CancellationRequestedAt, Outcome = EvaluationOutcome.Inconclusive,
                    ReasonCode = latest.CancellationRequestedAt is not null ? "sandbox_evaluation_cancelled" : "sandbox_evaluation_authority_changed" }, latest.Revision, CancellationToken.None);
        }
    }
    public async ValueTask<bool> CleanupEvaluationAsync(EvaluationSandboxExecution execution, CancellationToken ct = default)
    {
        var current = await EvaluationStore.FindSandboxEvaluationAsync(execution.TenantId, execution.ExecutionId, ct);
        if (current is null || current.HostScope != EvaluationHostScope || current.ExecutorId != execution.ExecutorId || current.AttemptId != execution.AttemptId
            || current.Phase == EvaluationSandboxPhase.AwaitingReview) return false;
        if (!await TryRetireTenantGenerationAsync(EvaluationLease(current), ct)) return false;
        var root = Path.Combine(options.CanonicalRoot(), ".evaluations", current.ExecutionId.ToString("N"));
        if (Directory.Exists(root)) CanonicalWorkspaceFilesystem.PurgeOwner(root, ct);
        return true;
    }
}
