using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Evaluations;

public sealed record EvaluationSandboxEvidence(Guid ExecutionId, long Revision, Guid ActorId, Guid ExecutorId, string CheckId, string Phase, Guid ApprovalRequestId, string ProfileId, long ProfileRevision, SandboxEvaluationInput InputKind,
    ScriptReference Script, string InputHash, EvaluationOutcome? Outcome, string? ReasonCode, int? ExitCode, long? DurationMs,
    string? OutputHash, bool CleanupPending, bool TerminationConfirmed, DateTimeOffset? CancellationRequestedAt);
public sealed record EvaluationSandboxCancel(Guid ExecutionId, long ExpectedExecutionRevision, long ExpectedRevision);

public sealed partial class TenantEvaluationService
{
    private async ValueTask<EvaluationFinding> PrepareSandboxCheck(EvaluationCheck check, ScriptVersion script, SubjectSnapshot subject,
        EvaluationAttempt? attempt, HumanSessionReference? human, CancellationToken ct)
    {
        if (attempt is null || subject.Run is not { IsTerminal: true } run || attempt.Hook is EvaluationHook.BeforeRun or EvaluationHook.Publication)
            return Finding(check, EvaluationOutcome.Inconclusive, "sandbox_evaluation_terminal_source_required", "Native Script execution requires readable terminal Run evidence. Before Run and Publication are unsupported.");
        if (nativeSandbox?.EvaluationEnabled != true || nativeAuthority is null)
            return Finding(check, EvaluationOutcome.Inconclusive, "sandbox_host_unavailable", "Enable and qualify the existing Sandbox host, then deliberately create a fresh evaluation.");
        try
        {
            var state = await State(attempt.ActorId, attempt.TenantId, human, ct);
            var profile = script.SandboxProfileId is null ? null : await ((ITenantSandboxProfileStore)security).FindTenantSandboxProfileAsync(attempt.TenantId, script.SandboxProfileId, ct: ct);
            if (profile is not { Enabled: true } || script.SandboxTimeoutSeconds > profile.MaxCommandSeconds || profile.MaxLifetimeSeconds < 120)
                throw new AccessDeniedException("sandbox_evaluation_profile_unavailable");
            var agent = await ((ITenantAgentStore)security).FindTenantAgentAsync(attempt.TenantId, run.AgentInstanceId, ct) ?? throw new AccessDeniedException("executor_unavailable");
            var at = time.GetUtcNow(); var id = ids.NewId(); var input = EvaluationSandboxAuthority.Input(run, script.SandboxInput); var inputHash = EvaluationRules.Hash(input);
            var reference = new SandboxEvaluationReference(attempt.AttemptId, id, check.CheckId, check.Script!, inputHash);
            var command = new SandboxCommandRequest(script.Body, Cwd: "/working", TimeoutSeconds: script.SandboxTimeoutSeconds, ProfileId: profile.Id);
            var prepared = new PreparedSandboxCommand(run.SessionId, profile.Id, profile.Revision, TenantApprovalRules.CanonicalSandboxCommand(command),
                0, null, profile.RegistryCredentialId, profile.RegistryCredentialRevision, profile.RegistrySecretRevision, HomeAccess.ReadOnly, Evaluation: reference);
            var operation = TenantApprovalRules.PrepareSandbox(ids.NewId(), run.AgentInstanceId, prepared, agent.Instance.Revision, agent.Instance.PersonaRevision);
            var snapshot = new TenantApprovalSnapshot("Evaluation · " + check.Title, Sandbox: new(profile.Name, profile.ImageReference, null, null, false,
                Math.Min(profile.Cpu, 1), Math.Min(profile.MemoryMiB, 512), script.SandboxTimeoutSeconds, 120, 120, Math.Min(profile.StorageBytes, 16L * 1024 * 1024)));
            var approval = new TenantApprovalRequest(ids.NewId(), attempt.TenantId, run.AgentRunId, run.SessionId, run.Admission.Activation.RootActivationId, agent.PrincipalId,
                human?.PrincipalId ?? attempt.LifecycleOrigin?.OriginalInitiatorId, ApprovalKind.Review, operation, TenantApprovalStatus.Pending, 1, at, at.AddHours(1), snapshot);
            // Persist the original authenticated session, not an execution identity copied from the source Run.
            var execution = new EvaluationSandboxExecution(attempt.TenantId, id, attempt.AttemptId, check.CheckId, attempt.ActorId, agent.PrincipalId, run.AgentRunId,
                run.Revision, run.SessionId, run.AgentInstanceId, check.Script!, script.SandboxInput, profile, input, inputHash, approval, human is null ? null : new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId),
                nativeSandbox.EvaluationHostScope, EvaluationSandboxPhase.AwaitingReview, 1, at, approval.ExpiresAt);
            await nativeAuthority.RequireAsync(execution, ct);
            if (!await store.TryReserveSandboxEvaluationAsync(Advance(state), state.Tenant.Revision, execution, attempt.Revision,
                Audit(state, attempt.ActorId, "approvals.request", approval.RequestId), human, ct)) throw new SecurityConflictException("stale_revision");
            return Finding(check, EvaluationOutcome.Inconclusive, "sandbox_approval_required", "Exact Sandbox execution Review is pending in Approvals. The subject Run remains unchanged.") with { SandboxExecutionId = id };
        }
        catch (Exception error) when (error is AccessDeniedException or SecurityConflictException)
        {
            var reason = error is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)error).ReasonCode;
            return Finding(check, EvaluationOutcome.Inconclusive, reason, "Native evaluation was not dispatched. Repair current source, profile or independent execution authority and request a fresh evaluation.");
        }
    }
    public async ValueTask<IReadOnlyList<EvaluationSandboxEvidence>> SandboxEvidenceAsync(Guid actor, Guid tenant, Guid attemptId, HumanSessionReference? human, CancellationToken ct = default)
    {
        var attempt = await ReadAsync(actor, tenant, attemptId, human, ct); var result = new List<EvaluationSandboxEvidence>();
        foreach (var id in attempt.Findings.Select(f => f.SandboxExecutionId).OfType<Guid>())
            if (await store.FindSandboxEvaluationAsync(tenant, id, ct) is { } execution)
                result.Add(new(execution.ExecutionId, execution.Revision, execution.ActorId, execution.ExecutorId, execution.CheckId, execution.Phase.ToString(), execution.Approval.RequestId, execution.Profile.Id, execution.Profile.Revision, execution.InputKind,
                    execution.Script, execution.InputHash, execution.Outcome, execution.ReasonCode, execution.ExitCode, execution.DurationMs,
                    execution.OutputHash, execution.CleanupPending, execution.TerminationConfirmed, execution.CancellationRequestedAt));
        return result;
    }
    public async ValueTask<IReadOnlyList<EvaluationSandboxEvidence>> CancelSandboxAsync(Guid actor, Guid tenant, Guid attemptId,
        EvaluationSandboxCancel request, HumanSessionReference human, CancellationToken ct = default)
    {
        _ = await ReadAsync(actor, tenant, attemptId, human, ct);
        var state = await State(actor, tenant, human, ct); Revision(state, request.ExpectedRevision);
        var execution = await store.FindSandboxEvaluationAsync(tenant, request.ExecutionId, ct) ?? throw new AccessDeniedException("evaluation_unavailable");
        if (execution.AttemptId != attemptId) throw new AccessDeniedException("evaluation_unavailable");
        if (actor != execution.ActorId) await Require(state, actor, "approvals.cancel", AccessScope.ForResource(tenant, "approval", execution.Approval.RequestId.ToString("D")), ct);
        if (execution.CancellationRequestedAt is not null || execution.Phase == EvaluationSandboxPhase.Settled) return await SandboxEvidenceAsync(actor, tenant, attemptId, human, ct);
        if (execution.Revision != request.ExpectedExecutionRevision) throw new SecurityConflictException("stale_revision");
        if (execution.Phase == EvaluationSandboxPhase.AwaitingReview) throw new ArgumentException("Cancel the exact pending Review in Approvals before execution begins.");
        var cancelled = execution with { Revision = execution.Revision + 1, CancellationRequestedAt = time.GetUtcNow() };
        if (!await store.TrySaveSandboxEvaluationAsync(cancelled, execution.Revision, ct, state.Tenant.Revision)) throw new SecurityConflictException("stale_revision");
        return await SandboxEvidenceAsync(actor, tenant, attemptId, human, ct);
    }
    private async ValueTask ContinueSandboxEvaluationsAsync(int limit, CancellationToken ct)
    {
        if (nativeSandbox is null || nativeAuthority is null) return;
        foreach (var observed in await store.ListSandboxEvaluationsAsync(limit, time.GetUtcNow(), ct))
        {
            var value = observed;
            if (value.Phase == EvaluationSandboxPhase.AwaitingReview)
            {
                // Initial findings must be durably Pending before a Review can continue execution.
                // An abandoned initial reservation is handled by the existing attempt recovery pass.
                if ((await store.FindAttemptAsync(value.TenantId, value.AttemptId, ct))?.Status == EvaluationStatus.Running) continue;
                var approval = await ((ITenantApprovalStore)security).FindApprovalAsync(value.TenantId, value.Approval.RequestId, ct);
                if (approval?.Status == TenantApprovalStatus.Pending && value.ExpiresAt > time.GetUtcNow())
                {
                    try
                    {
                        await nativeAuthority.RequireAsync(value, ct);
                        var waiting = value with { Revision = value.Revision + 1, RetryCleanupAt = time.GetUtcNow().AddSeconds(60) };
                        _ = await store.TrySaveSandboxEvaluationAsync(waiting, value.Revision, ct);
                        continue;
                    }
                    catch (AccessDeniedException) { /* Settle unavailable input/authority below, without dispatch. */ }
                }
                try
                {
                    var state = await nativeAuthority.RequireAsync(value, ct, approved: true);
                    value = value with { Phase = EvaluationSandboxPhase.Reserved, Revision = value.Revision + 1, RetryCleanupAt = null, DispatchClaim = ids.NewId(), DispatchExpiresAt = time.GetUtcNow().AddMinutes(2) };
                    if (!await store.TrySaveSandboxEvaluationAsync(value, observed.Revision, ct, state.Tenant.Revision)) continue;
                    await nativeSandbox.ExecuteEvaluationAsync(value.TenantId, value.ExecutionId, value.DispatchClaim!.Value, ct);
                    value = (await store.FindSandboxEvaluationAsync(value.TenantId, value.ExecutionId, ct))!;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is AccessDeniedException or SecurityConflictException)
                {
                    value = await store.FindSandboxEvaluationAsync(value.TenantId, value.ExecutionId, ct) ?? observed;
                    if (value.Phase != EvaluationSandboxPhase.AwaitingReview) continue;
                    value = value with { Phase = EvaluationSandboxPhase.Settled, Revision = value.Revision + 1, Outcome = EvaluationOutcome.Inconclusive,
                        ReasonCode = approval?.Status is TenantApprovalStatus.Rejected or TenantApprovalStatus.Cancelled ? "sandbox_review_declined" : "sandbox_evaluation_authority_changed", InputJson = null };
                    if (!await store.TrySaveSandboxEvaluationAsync(value, value.Revision - 1, ct)) continue;
                }
            }
            else if (value.Phase is EvaluationSandboxPhase.Reserved or EvaluationSandboxPhase.Dispatching)
            {
                if (value.DispatchExpiresAt > time.GetUtcNow()) continue;
                // A prior process owned dispatch. Recovery cleans that identity; it cannot execute again.
                var clean = await nativeSandbox.CleanupEvaluationAsync(value, ct);
                value = value with { Phase = EvaluationSandboxPhase.Settled, Revision = value.Revision + 1, Outcome = EvaluationOutcome.Inconclusive,
                    ReasonCode = "sandbox_evaluation_interrupted", InputJson = null, CleanupPending = !clean, TerminationConfirmed = clean,
                    RetryCleanupAt = clean ? null : time.GetUtcNow().AddSeconds(30) };
                if (!await store.TrySaveSandboxEvaluationAsync(value, value.Revision - 1, ct)) continue;
            }
            if (value.Phase == EvaluationSandboxPhase.Settled && value.CleanupPending)
            {
                var clean = await nativeSandbox.CleanupEvaluationAsync(value, ct);
                value = value with { Revision = value.Revision + 1, CleanupPending = !clean, CleanupPasses = value.CleanupPasses + 1,
                    TerminationConfirmed = clean || value.TerminationConfirmed,
                    RetryCleanupAt = clean ? null : time.GetUtcNow().AddSeconds(Math.Min(300, 30 * (value.CleanupPasses + 1))) };
                if (!await store.TrySaveSandboxEvaluationAsync(value, value.Revision - 1, ct)) continue;
            }
            if (value.Phase == EvaluationSandboxPhase.Settled && !value.FindingPublished)
            {
                var published = await SettleSandboxFinding(value, ct);
                value = (await store.FindSandboxEvaluationAsync(value.TenantId, value.ExecutionId, ct))!;
                value = value with { Revision = value.Revision + 1, FindingPublished = published || value.FindingPublished, RetryCleanupAt = published && !value.CleanupPending ? null : time.GetUtcNow().AddSeconds(30) };
                _ = await store.TrySaveSandboxEvaluationAsync(value, value.Revision - 1, ct);
            }
        }
    }
    private async ValueTask<bool> SettleSandboxFinding(EvaluationSandboxExecution execution, CancellationToken ct)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            var attempt = await store.FindAttemptAsync(execution.TenantId, execution.AttemptId, ct);
            if (attempt is not { Status: EvaluationStatus.Pending } || !attempt.Findings.Any(f => f.SandboxExecutionId == execution.ExecutionId && f.ReasonCode == "sandbox_approval_required")) return true;
            // Merge only this native finding into the latest immutable evidence. Never rerun a check.
            var findings = attempt.Findings.Select(f => f.SandboxExecutionId == execution.ExecutionId ? EvaluationSandboxRules.Finding(f, execution) : f).ToArray();
            var pending = findings.Any(f => f.ReasonCode is "review_required" or "sandbox_approval_required");
            try
            {
                await Settle(attempt, findings, pending ? EvaluationStatus.Pending : EvaluationStatus.Completed, null,
                    execution.Human is null ? null : new HumanSessionReference(execution.Human.AccountId, execution.Human.AuthenticationSessionId, execution.Human.PrincipalId), ct, native: true);
                return true;
            }
            catch (SecurityConflictException) { /* Reread and revalidate before retrying a contended write. */ }
            catch (AccessDeniedException)
            {
                // Only confirmed unavailable authority/source can use technical recovery. Its CAS
                // must not invalidate findings that committed after this observation.
                if (await store.TryRecordSandboxSettlementUnavailableAsync(execution.TenantId, execution.ExecutionId, attempt.Revision, ct)) return true;
            }
        }
        // Leave bounded contention pending for the existing sweep; execution is already settled.
        return false;
    }
}
