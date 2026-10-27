using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Evaluations;

// Shared by approval, admission and the provider boundary. Never resolves a live source Run claim.
public sealed class EvaluationSandboxAuthority(IIamStore security, ITenantEvaluationStore evaluations, ITenantRunStore runs,
    ITenantSessionStore sessions, ITenantSandboxProfileStore profiles, ITenantApprovalStore approvals,
    ITenantApprovedOperationStore decisions, IamAuthorizer iam, IdentityService identity, TimeProvider time)
{
    public static string Input(AgentRun run, SandboxEvaluationInput kind)
    {
        if (kind == SandboxEvaluationInput.RunOutput && string.IsNullOrWhiteSpace(run.Result?.Text)
            || kind == SandboxEvaluationInput.AdmittedInput && string.IsNullOrWhiteSpace(run.Admission.TaskIntent?.Text))
            throw new AccessDeniedException("sandbox_evaluation_historical_input_unavailable");
        var input = JsonSerializer.Serialize(new { contractVersion = 1, runId = run.AgentRunId, revision = run.Revision,
            status = run.Status.ToString(), inputKind = kind.ToString(), input = kind == SandboxEvaluationInput.AdmittedInput ? run.Admission.TaskIntent?.Text : null, output = kind == SandboxEvaluationInput.RunOutput ? run.Result?.Text : null,
            outcome = run.Result?.OutcomeKind.ToString(), configurationHash = EvaluationRules.Hash(run.Admission.Configuration) });
        if (System.Text.Encoding.UTF8.GetByteCount(input) > 65536) throw new AccessDeniedException("sandbox_evaluation_input_too_large");
        return input;
    }
    public async ValueTask<TenantSecurityState> RequireAsync(EvaluationSandboxExecution execution, CancellationToken ct,
        bool approved = false, Guid? dispatchClaim = null)
    {
        if (execution.CancellationRequestedAt is not null) throw new AccessDeniedException("sandbox_evaluation_cancelled");
        var tenant = execution.TenantId;
        var state = execution.Human is { } human
            ? await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, new HumanSessionReference(human.AccountId, human.AuthenticationSessionId, human.PrincipalId), ct)
            : await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var attempt = await evaluations.FindAttemptAsync(tenant, execution.AttemptId, ct) ?? throw new AccessDeniedException("evaluation_unavailable");
        var suite = await evaluations.FindSuiteAsync(tenant, attempt.SuiteId, attempt.SuiteVersion, ct) ?? throw new AccessDeniedException("evaluation_suite_unavailable");
        var script = await evaluations.FindScriptAsync(tenant, execution.Script.ScriptId, execution.Script.Version, ct) ?? throw new AccessDeniedException("script_unavailable");
        var run = await runs.FindTenantRunAsync(tenant, execution.SubjectRunId, ct) ?? throw new AccessDeniedException("evaluation_source_unavailable");
        if (attempt.Status is EvaluationStatus.Completed or EvaluationStatus.Cancelled or EvaluationStatus.Indeterminate
            || !run.IsTerminal || run.Revision != execution.SubjectRevision || attempt.SubjectId != run.AgentRunId
            || attempt.SubjectKind != EvaluationSubjectKind.AgentRun || attempt.SubjectRevision != run.Revision || attempt.ActorId != execution.ActorId
            || run.Owner.ExecutorPrincipalId != execution.ExecutorId || run.SessionId != execution.SubjectSessionId || run.AgentInstanceId != execution.InstanceId
            || await sessions.FindTenantSessionAsync(tenant, run.SessionId, ct) is null
            || attempt.SourceFingerprint != EvaluationRules.Hash(new { run.AgentRunId, run.Revision, run.Status })
            || suite.ContentHash != attempt.SuiteHash || suite.Archived || (await evaluations.FindSuiteAsync(tenant, suite.SuiteId, ct: ct))?.Archived != false
            || !suite.Checks.Any(c => c.CheckId == execution.CheckId && c.Script == execution.Script)
            || script.Kind != ScriptKind.Sandbox || script.ContentHash != execution.Script.ContentHash || script.Archived
            || (await evaluations.FindScriptAsync(tenant, script.ScriptId, ct: ct))?.Archived != false
            || !script.Hooks.Contains(attempt.Hook) || script.SandboxInput != execution.InputKind || script.SandboxProfileId != execution.Profile.Id
            || attempt.Hook is EvaluationHook.BeforeRun or EvaluationHook.Publication
            || execution.Human is { } actor && actor.PrincipalId != execution.ActorId
            || execution.ExpiresAt <= time.GetUtcNow()) throw new AccessDeniedException("sandbox_evaluation_witness_changed");
        if (Input(run, execution.InputKind) is { } input && EvaluationRules.Hash(input) != execution.InputHash)
            throw new AccessDeniedException("sandbox_evaluation_input_changed");
        var profile = await profiles.FindTenantSandboxProfileAsync(tenant, execution.Profile.Id, ct: ct);
        if (profile is null || !profile.Enabled || profile != execution.Profile || profile.TenantId != tenant
            || script.SandboxTimeoutSeconds > profile.MaxCommandSeconds)
            throw new AccessDeniedException("sandbox_evaluation_profile_changed");
        var executor = await ((ITenantAgentStore)security).FindTenantAgentAsync(tenant, execution.InstanceId, ct) ?? throw new AccessDeniedException("executor_unavailable");
        if (executor.PrincipalId != execution.ExecutorId) throw new AccessDeniedException("executor_unavailable");
        TenantSessionRules.RequireExecutor(state, executor);
        await ActorAsync(state, execution.ActorId, attempt, execution, ct);
        if (execution.ExecutorId != execution.ActorId) await ActorAsync(state, execution.ExecutorId, attempt, execution, ct);
        if (attempt.LifecycleOrigin is { OriginalInitiatorId: { } initiator }) await SourceAsync(state, initiator, execution, ct);
        if (approved)
        {
            if (attempt.Status != EvaluationStatus.Pending) throw new AccessDeniedException("sandbox_evaluation_not_ready");
            var request = await approvals.FindApprovalAsync(tenant, execution.Approval.RequestId, ct) ?? throw new AccessDeniedException("approval_unavailable");
            var decision = await decisions.FindApprovalDecisionAsync(tenant, request.RequestId, ct) ?? throw new AccessDeniedException("approval_unavailable");
            if (request.Status != TenantApprovalStatus.Approved || request.Kind != ApprovalKind.Review || request.Operation != execution.Approval.Operation
                || decision.Status != TenantApprovalStatus.Approved || decision.ActionHash != request.Operation.ActionHash || decision.RequestRevision != request.Revision)
                throw new AccessDeniedException("sandbox_evaluation_review_required");
            await ReviewerAsync(state, request, decision.HumanPrincipalId, ct);
        }
        if (dispatchClaim is { } claim)
        {
            var saved = await evaluations.FindSandboxEvaluationAsync(tenant, execution.ExecutionId, ct);
            if (saved is null || saved.ExecutionId != execution.ExecutionId || saved.AttemptId != execution.AttemptId || saved.Revision < execution.Revision
                || saved.CancellationRequestedAt is not null || saved.DispatchClaim != claim || saved.DispatchExpiresAt <= time.GetUtcNow()
                || saved.Phase is not (EvaluationSandboxPhase.Reserved or EvaluationSandboxPhase.Dispatching))
                throw new AccessDeniedException("sandbox_evaluation_dispatch_unavailable");
        }
        return state;
    }
    public async ValueTask ReadAsync(TenantSecurityState state, TenantApprovalRequest request, Guid reader, CancellationToken ct)
    {
        var reference = request.Operation.SandboxCommand?.Evaluation ?? throw new AccessDeniedException("approval_unavailable");
        var execution = await evaluations.FindSandboxEvaluationAsync(state.Tenant.TenantId, reference.ExecutionId, ct) ?? throw new AccessDeniedException("approval_unavailable");
        if (execution.Approval.Operation != request.Operation || execution.Approval.RequestId != request.RequestId) throw new AccessDeniedException("approval_unavailable");
        var attempt = await evaluations.FindAttemptAsync(state.Tenant.TenantId, execution.AttemptId, ct) ?? throw new AccessDeniedException("approval_unavailable");
        await Need(state, reader, "approvals.read", AccessScope.ForResource(request.TenantId, "approval", request.RequestId.ToString("D")), ct);
        await Need(state, reader, "evaluations.read", EvaluationStoreRules.Scope(request.TenantId, attempt.SuiteId), ct);
        await Need(state, reader, "scripts.read", EvaluationStoreRules.Scope(request.TenantId, reference.Script.ScriptId, true), ct);
        await Need(state, reader, "sandbox.read", TenantSandboxProfileRules.Scope(request.TenantId, execution.Profile.Id), ct);
        await SourceAsync(state, reader, execution, ct);
    }
    public async ValueTask ReviewerAsync(TenantSecurityState state, TenantApprovalRequest request, Guid reviewer, CancellationToken ct)
    {
        await ReadAsync(state, request, reviewer, ct);
        var reference = request.Operation.SandboxCommand!.Evaluation!;
        var execution = await evaluations.FindSandboxEvaluationAsync(request.TenantId, reference.ExecutionId, ct) ?? throw new AccessDeniedException("approval_unavailable");
        var attempt = await evaluations.FindAttemptAsync(request.TenantId, execution.AttemptId, ct) ?? throw new AccessDeniedException("approval_unavailable");
        await ReviewerPermissionsAsync(state, request, execution, attempt, reviewer, iam, ct);
    }
    public static async ValueTask ReviewerPermissionsAsync(TenantSecurityState state, TenantApprovalRequest request, EvaluationSandboxExecution execution,
        EvaluationAttempt attempt, Guid reviewer, IamAuthorizer authorizer, CancellationToken ct)
    {
        foreach (var (permission, scope) in new[] {
            ("approvals.read", AccessScope.ForResource(request.TenantId, "approval", request.RequestId.ToString("D"))),
            ("approvals.approve", AccessScope.ForResource(request.TenantId, "approval", request.RequestId.ToString("D"))),
            ("sandbox.execute", AccessScope.ForResource(request.TenantId, "agent", request.Operation.InstanceId.ToString("D"))),
            ("sandbox.read", TenantSandboxProfileRules.Scope(request.TenantId, execution.Profile.Id)),
            ("evaluations.read", EvaluationStoreRules.Scope(request.TenantId, attempt.SuiteId)),
            ("scripts.read", EvaluationStoreRules.Scope(request.TenantId, execution.Script.ScriptId, true)),
            ("agent-runs.read", TenantRunRules.Scope(request.TenantId, execution.SubjectRunId)),
            ("sessions.read", TenantSessionRules.Scope(request.TenantId, execution.SubjectSessionId)),
            ("agents.read", AccessScope.ForResource(request.TenantId, "agent", execution.InstanceId.ToString("D"))) })
        {
            var decision = await authorizer.CheckTenantAsync(state, reviewer, PermissionCatalog.Get(permission), scope, ct);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get("sandbox.execute")).IndependentReview
            && (reviewer == request.ExecutorPrincipalId || reviewer == request.OriginalInitiatorPrincipalId)) throw new AccessDeniedException("independent_review_required");
    }
    public async ValueTask ActorAsync(TenantSecurityState state, Guid actor, EvaluationAttempt attempt, EvaluationSandboxExecution execution, CancellationToken ct)
    {
        await Need(state, actor, "evaluations.read", EvaluationStoreRules.Scope(execution.TenantId, attempt.SuiteId), ct);
        await Need(state, actor, "evaluations.run", EvaluationStoreRules.Scope(execution.TenantId, attempt.SuiteId), ct);
        await Need(state, actor, "scripts.read", EvaluationStoreRules.Scope(execution.TenantId, execution.Script.ScriptId, true), ct);
        await Need(state, actor, "scripts.execute", EvaluationStoreRules.Scope(execution.TenantId, execution.Script.ScriptId, true), ct);
        await Need(state, actor, "sandbox.read", TenantSandboxProfileRules.Scope(execution.TenantId, execution.Profile.Id), ct);
        await Need(state, actor, "sandbox.execute", AccessScope.ForResource(execution.TenantId, "agent", execution.InstanceId.ToString("D")), ct);
        await SourceAsync(state, actor, execution, ct);
        if (execution.Profile.RegistryCredentialId is { } credential)
            await Need(state, actor, "credentials.use", AccessScope.ForResource(execution.TenantId, "credential", credential.ToString("D")), ct);
    }
    private async ValueTask SourceAsync(TenantSecurityState state, Guid actor, EvaluationSandboxExecution execution, CancellationToken ct)
    {
        await Need(state, actor, "agent-runs.read", TenantRunRules.Scope(execution.TenantId, execution.SubjectRunId), ct);
        await Need(state, actor, "sessions.read", TenantSessionRules.Scope(execution.TenantId, execution.SubjectSessionId), ct);
        await Need(state, actor, "agents.read", AccessScope.ForResource(execution.TenantId, "agent", execution.InstanceId.ToString("D")), ct);
    }
    private async ValueTask Need(TenantSecurityState state, Guid actor, string permission, AccessScope scope, CancellationToken ct)
    {
        var result = await iam.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct);
        if (!result.Allowed) throw new AccessDeniedException(result.ReasonCode);
    }
}
