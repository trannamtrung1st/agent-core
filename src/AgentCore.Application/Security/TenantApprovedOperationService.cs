using AgentCore.Application.Ports;
using AgentCore.Application.Admin;
using AgentCore.Application.Tools;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovedOperationService(IIamStore security,
    ITenantRunStore runs,
    ITenantAgentStore agents,
    ITenantApprovalStore approvals,
    ITenantApprovedOperationStore effects,
    TenantOperationAuthorizer operations,
    TenantRunConfigurationResolver configuration, IIdGenerator ids, TimeProvider time, ITenantModelCatalogReader modelCatalogs, ProviderAliasSet aliases,
    TenantSandboxControlOperationTarget? controlTargets = null,
    TenantSandboxRuntimeReadService? sandboxReads = null,
    TenantCredentialRuntimeReadService? credentialReads = null,
    TenantArtifactRuntimeReadService? artifactReads = null,
    TenantWorkspaceRuntimeReadService? workspaceReads = null,
    TenantArtifactSourceTarget? artifactSources = null,
    TenantAttachmentRuntimeContextService? attachmentReads = null,
    TenantAccountRuntimeReadService? accountReads = null,
    TenantMemoryOperationService? memoryOperations = null,
    TenantContinuityReadService? continuityReads = null,
    TenantExperienceOperationService? experienceOperations = null,
    TenantAutomationOperationTarget? automationTargets = null,
    TenantManagementOperationTarget? managementTargets = null,
    TenantTaskDelegationTarget? taskTargets = null)
{
    // Trusted server frame only. The saved request supplies every action parameter and target.
    public async ValueTask<TenantOperationReceipt> ExecuteAsync(TenantRunReference reference, Guid requestId, Guid operationId,
        Guid generation, string actionHash, CancellationToken ct = default)
    {
        try { return await ExecuteCoreAsync(reference, requestId, operationId, generation, actionHash, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is AccessDeniedException or SecurityConflictException)
        {
            var reason = ex is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)ex).ReasonCode;
            var request = await approvals.FindApprovalAsync(reference.TenantId, requestId, ct).ConfigureAwait(false);
            var permission = request?.Operation.OperationId == operationId ? request.Permission : "";
            await operations.RecordFailureAsync(reference, permission, reason, ExecutionAuthorizationPurpose.EffectEligibility, ct).ConfigureAwait(false);
            throw;
        }
    }
    private async ValueTask<TenantOperationReceipt> ExecuteCoreAsync(TenantRunReference reference, Guid requestId, Guid operationId,
        Guid generation, string actionHash, CancellationToken ct)
    {
        var run = await runs.FindTenantRunAsync(reference.TenantId, reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        var context = run.Admission.ExecutionContext;
        if (context is null || context.TenantId != reference.TenantId || run.IsTerminal) throw new AccessDeniedException("run_unavailable");
        var state = await security.ReadTenantAsync(reference.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var agent = await agents.FindTenantAgentAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("executor_unavailable");
        if (state.Tenant.Status != IdentityStatus.Active || agent.PrincipalId != context.AgentPrincipalId) throw new AccessDeniedException("executor_unavailable");
        TenantSessionRules.RequireExecutor(state, agent);
        var request = await approvals.FindApprovalAsync(reference.TenantId, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var source = request.RunId != run.AgentRunId ? await runs.FindTenantRunAsync(reference.TenantId, request.RunId, ct).ConfigureAwait(false) : null;
        TenantApprovalContinuationRules.Require(request, run, source);
        if (request.Operation.OperationId != operationId || request.Operation.ActionHash != actionHash
            || request.ExecutorPrincipalId != context.AgentPrincipalId || request.RootActivationId != context.RootActivationId)
            throw new SecurityConflictException("operation_mismatch");
        var existing = await effects.FindOperationReceiptAsync(reference.TenantId, operationId, ct).ConfigureAwait(false);
        if (existing is not null) return Existing(existing, request, run);
        if (request.Operation.TaskDelegation is not null) return await ExecuteTaskDelegationAsync(reference, request, run, source, state, generation, ct).ConfigureAwait(false);
        if (request.Operation.WorkspaceMutation is not null) throw new SecurityConflictException("workspace_effect_unsettled");
        if (request.Operation.HttpRequest is not null) throw new SecurityConflictException("http_effect_unsettled");
        if (request.Operation.AccountAction is not null) throw new SecurityConflictException("account_effect_unsettled");
        if (request.Operation.ArtifactPublication is not null) return await ExecuteArtifactAsync(reference, request, run, source, state, agent, generation, ct).ConfigureAwait(false);
        if (request.Operation.AutomationMutation is not null) return await ExecuteAutomationMutationAsync(reference, request, run, source, state, generation, ct).ConfigureAwait(false);
        if (request.Operation.ManagementMutation is not null) return await ExecuteManagementMutationAsync(reference, request, run, source, state, generation, ct).ConfigureAwait(false);
        if (request.Operation.ManagementRead is not null) return await ExecuteManagementReadAsync(reference, request, run, source, state, generation, ct).ConfigureAwait(false);
        if (request.Operation.AutomationRead is not null) return await ExecuteAutomationReadAsync(reference, request, run, source, state, generation, ct).ConfigureAwait(false);
        if (request.Operation.ExperienceOperation is not null) return await ExecuteExperienceAsync(reference, request, run, source, state, generation, ct).ConfigureAwait(false);
        if (request.Operation.ContinuityRead is not null) return await ExecuteContinuityAsync(reference, request, run, source, state, generation, ct).ConfigureAwait(false);
        if (request.Operation.MemoryOperation is not null) return await ExecuteMemoryAsync(reference, request, run, source, state, generation, ct).ConfigureAwait(false);
        if (request.Operation.WorkspaceRead is not null) return await ExecuteWorkspaceReadAsync(reference, request, run, source, state, generation, ct).ConfigureAwait(false);
        if (request.Operation.AttachmentRead is not null) return await ExecuteAttachmentReadAsync(reference, request, run, source, state, generation, ct).ConfigureAwait(false);
        if (request.Operation.ArtifactVerification is not null) return await ExecuteArtifactVerificationAsync(reference, request, run, source, state, generation, ct).ConfigureAwait(false);
        if (request.Operation.AccountRead is not null) return await ExecuteAccountReadAsync(reference, request, run, source, state, generation, ct).ConfigureAwait(false);
        if (request.Operation.CredentialRead is not null) return await ExecuteMetadataReadAsync(reference, request, run, source, state, generation, ct).ConfigureAwait(false);
        if (request.Operation.SandboxRead is not null) return await ExecuteReadAsync(reference, request, run, source, state, agent, generation, ct).ConfigureAwait(false);
        if (request.Operation.SandboxControl is not null) return await ExecuteControlAsync(reference, request, run, source, state, agent, generation, ct).ConfigureAwait(false);
        if (!request.Operation.IsLocalConfiguration) throw new AccessDeniedException("tenant_effect_unavailable");
        var decision = await effects.FindApprovalDecisionAsync(reference.TenantId, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(reference.TenantId, requestId, ct).ConfigureAwait(false);
        var commit = new TenantOperationCommit(requestId, operationId, run.AgentRunId, generation, run.Revision, request.Revision,
            decision.DecisionId, grant?.Revision, actionHash, TenantOperationOutcome.Completed, "operation_completed");
        TenantLocalEffectRules.Validate(request, decision, grant, run, commit, source);
        var at = time.GetUtcNow();
        if (run.Claim!.LeaseExpiresAtUtc <= at) throw new SecurityConflictException("execution_claim_expired");
        if (run.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new SecurityConflictException("run_effect_uncertain");
        if (run.SideEffect.ToolCallId == "operation:" + operationId.ToString("D") && run.SideEffect.Disposition == AgentRunSideEffectDisposition.Succeeded)
            throw new SecurityConflictException("operation_receipt_missing");
        string? reason = grant is not null && grant.ExpiresAt <= at ? "grant_expired" : request.ExpiresAt <= at ? "approval_expired" : null;
        var target = AccessScope.ForResource(reference.TenantId, request.ResourceType, request.ResourceId);
        if (reason is null && !await operations.QualifiesAsync(state, request.OriginalInitiatorPrincipalId, decision.HumanPrincipalId,
            PermissionCatalog.Get(request.Permission), target, request.Kind, ct).ConfigureAwait(false)) reason = "grantor_authority_lost";
        if (reason is null)
        {
            try { await operations.ResolveAsync(reference, ct).ConfigureAwait(false); }
            catch (AccessDeniedException ex) when (ex.ReasonCode == "requester_unavailable") { reason = ex.ReasonCode; }
        }
        if (reason is null)
        {
            var eligibility = await operations.Check(state, run, request.Permission, target, ct).ConfigureAwait(false);
            if (eligibility.Decision == OperationDecisionKind.Denied || request.Kind == ApprovalKind.Review && eligibility.ApprovalKind == ApprovalKind.ExceptionalGrant)
                reason = "operation_authority_changed";
        }
        if (reason is null)
        {
            var current = await configuration.ResolveAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
            if (current.Agent.Instance.Revision != agent.Instance.Revision) throw new SecurityConflictException("stale_revision");
            reason = TenantLocalEffectRules.Eligibility(run, agent, current.Configuration.Configuration.Definition);
            if (reason is null && request.Operation.Preferences is { } preferences)
            {
                var (latest, baseline) = await configuration.BaselineAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
                if (latest.Instance.Revision != agent.Instance.Revision) throw new SecurityConflictException("stale_revision");
                var patch = TenantPreferenceToolContract.Patch(preferences);
                var models = await modelCatalogs.ReadAsync(state, ct).ConfigureAwait(false);
                try { _ = InstanceSettingsRules.ResolvePatch(baseline, latest.Instance.SettingsOverrides ?? new(), "conversationPolicy", patch.Set, patch.Clear, models, aliases); }
                catch (AgentCoreException) { reason = "operation_configuration_changed"; }
            }
        }
        if (reason is null && (agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision))
            reason = "operation_witness_changed";
        if (reason is not null) commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = reason };
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var audit = new AuthorizationAudit(ids.NewId(), reference.TenantId, context.AgentPrincipalId, TenantLocalEffectRules.AuditAction(request.Operation), request.ResourceId,
            reason is null ? "allowed" : "denied", commit.ReasonCode, at);
        var receipt = await effects.TryCommitLocalEffectAsync(next, state.Tenant.Revision, commit, audit, ct).ConfigureAwait(false);
        if (receipt is not null) return receipt;
        // Another exact caller may have committed while this caller was checking current authority.
        await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
        existing = await effects.FindOperationReceiptAsync(reference.TenantId, operationId, ct).ConfigureAwait(false);
        return existing is not null ? Existing(existing, request, run) : throw new SecurityConflictException("stale_revision");
    }

    private static TenantOperationReceipt Existing(TenantOperationReceipt receipt, TenantApprovalRequest request, AgentRun run)
    {
        if (receipt.TenantId != request.TenantId || receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId
            || receipt.ExecutorPrincipalId != run.Admission.ExecutionContext!.AgentPrincipalId || receipt.RunId != run.AgentRunId || receipt.ActionHash != request.Operation.ActionHash
            || receipt.SessionId != run.SessionId || receipt.RootActivationId != run.Admission.Activation.RootActivationId || receipt.InstanceId != request.Operation.InstanceId)
            throw new SecurityConflictException("operation_mismatch");
        return receipt;
    }
}
