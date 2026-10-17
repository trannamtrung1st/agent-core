using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Trusted off-mailbox execution only. Model arguments never supply a target, secret, principal or grant.
public sealed class TenantCredentialCheckService(IIamStore security, ITenantRunStore runs, ITenantApprovalStore approvals,
    ITenantApprovedOperationStore receipts, ITenantCredentialCheckStore checks, TenantCredentialOperationTarget targets,
    TenantOperationAuthorizer operations, IamAuthorizer authorizer, ITenantCredentialOAuthVerifier verifier, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantOperationReceipt> ExecuteAsync(TenantRunReference reference, Guid requestId, Guid operationId,
        Guid generation, string actionHash, CancellationToken ct = default)
    {
        try { return await ExecuteCore(reference, requestId, operationId, generation, actionHash, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is AccessDeniedException or SecurityConflictException)
        {
            var reason = ex is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)ex).ReasonCode;
            await operations.RecordFailureAsync(reference, "credentials.use", reason, ExecutionAuthorizationPurpose.EffectEligibility, ct).ConfigureAwait(false); throw;
        }
    }

    private async ValueTask<TenantOperationReceipt> ExecuteCore(TenantRunReference reference, Guid requestId, Guid operationId,
        Guid generation, string actionHash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var run = await runs.FindTenantRunAsync(reference.TenantId, reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        var request = await approvals.FindApprovalAsync(reference.TenantId, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var source = request.RunId == run.AgentRunId ? null : await runs.FindTenantRunAsync(reference.TenantId, request.RunId, ct).ConfigureAwait(false);
        TenantRunContinuationRules.Require(run, request, source);
        if (request.Status != TenantApprovalStatus.Approved || request.Operation.Kind != TenantOperationKind.CredentialConnectionCheck
            || request.Operation.OperationId != operationId || request.Operation.ActionHash != actionHash)
            throw new SecurityConflictException("operation_mismatch");
        if (run.Status == AgentRunStatus.Running && run.Claim?.Generation != generation) throw new AccessDeniedException("tenant_runtime_unavailable");
        var old = await receipts.FindOperationReceiptAsync(reference.TenantId, operationId, ct).ConfigureAwait(false);
        if (old is not null) return Matching(old, request, run);
        if (run.Status != AgentRunStatus.Running || run.CancellationRequested || run.Claim?.Generation != generation
            || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow()) throw new AccessDeniedException("tenant_runtime_unavailable");
        if (await checks.FindCredentialCheckDispatchAsync(reference.TenantId, operationId, ct).ConfigureAwait(false) is not null)
            throw new SecurityConflictException("credential_check_in_progress");
        var state = await security.ReadTenantAsync(reference.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var decision = await receipts.FindApprovalDecisionAsync(reference.TenantId, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(reference.TenantId, requestId, ct).ConfigureAwait(false);
        var commit = new TenantOperationCommit(requestId, operationId, run.AgentRunId, generation, run.Revision, request.Revision,
            decision.DecisionId, grant?.Revision, actionHash, TenantOperationOutcome.Completed, "operation_completed");
        TenantCredentialCheckRules.Start(request, decision, grant, run, commit, source);
        var at = time.GetUtcNow();
        string? reason = request.ExpiresAt <= at ? "approval_expired" : grant?.ExpiresAt <= at ? "grant_expired" : null;
        TenantCredentialBinding? bound = null; CredentialOAuthDescriptor? descriptor = null;
        try
        {
            var (_, current) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
            if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
            if (reason is null && !await operations.QualifiesAsync(state, request.OriginalInitiatorPrincipalId, decision.HumanPrincipalId,
                PermissionCatalog.Get(request.Permission), AccessScope.ForResource(reference.TenantId, request.ResourceType, request.ResourceId), request.Kind, ct).ConfigureAwait(false)) reason = "grantor_authority_lost";
            if (reason is null)
            {
                var eligibility = await operations.Check(state, run, request.Permission, AccessScope.ForResource(reference.TenantId, request.ResourceType, request.ResourceId), ct).ConfigureAwait(false);
                if (eligibility.Decision == OperationDecisionKind.Denied || request.Kind == ApprovalKind.Review && eligibility.ApprovalKind == ApprovalKind.ExceptionalGrant) reason = "operation_authority_changed";
            }
            if (reason is null)
            {
                var read = await authorizer.CheckTenantAsync(state, request.ExecutorPrincipalId, PermissionCatalog.Get("credentials.read"),
                    AccessScope.ForResource(reference.TenantId, request.ResourceType, request.ResourceId), ct).ConfigureAwait(false);
                if (!read.Allowed) reason = "executor_read_required";
            }
            if (reason is null)
            {
                var (agent, binding) = await targets.ResolveAsync(run, request.Operation.CredentialCheck!.Reference, ct).ConfigureAwait(false);
                TenantApprovalRules.Validate(request, run, agent); TenantCredentialOperationRules.RequireWitness(request.Operation, binding); bound = binding;
                descriptor = verifier.Describe(binding.Credential.Value.Purpose);
                reason = !descriptor.Configured ? "oauth_not_configured" : !descriptor.HostEnabled ? "oauth_host_disabled"
                    : descriptor.Synthetic != (binding.Credential.Value.EmailAccount?.Synthetic ?? binding.Credential.Value.DriveAccount?.Synthetic ?? false) ? "oauth_client_changed" : null;
            }
        }
        catch (AccessDeniedException e) { reason = e.ReasonCode; }
        catch (SecurityConflictException e) when (e.ReasonCode != "stale_revision") { reason = e.ReasonCode; }
        catch (AgentCore.Application.Sessions.AgentCoreException) { reason = "operation_configuration_changed"; }
        if (reason is not null) { reason = TenantCredentialCheckRules.Denial(reason); commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = reason }; }
        var duration = run.Claim.LeaseExpiresAtUtc - time.GetUtcNow() - TimeSpan.FromSeconds(1);
        if (duration <= TimeSpan.Zero) throw new SecurityConflictException("execution_claim_expired");
        if (duration > TimeSpan.FromSeconds(30)) duration = TimeSpan.FromSeconds(30);
        var audit = new AuthorizationAudit(ids.NewId(), request.TenantId, request.ExecutorPrincipalId,
            reason is null ? TenantCredentialCheckRules.DispatchAction : TenantCredentialCheckRules.Action, request.ResourceId,
            reason is null ? "allowed" : "denied", reason ?? "credential_check_dispatched", time.GetUtcNow());
        var start = await checks.TryStartCredentialCheckAsync(Next(state), state.Tenant.Revision, commit, audit, ct).ConfigureAwait(false);
        if (start?.Receipt is { } deniedReceipt) return Matching(deniedReceipt, request, run);
        if (start?.Dispatch is not { } dispatch)
        {
            old = await receipts.FindOperationReceiptAsync(reference.TenantId, operationId, ct).ConfigureAwait(false);
            if (old is not null) return Matching(old, request, run);
            throw new SecurityConflictException(await checks.FindCredentialCheckDispatchAsync(reference.TenantId, operationId, ct).ConfigureAwait(false) is null ? "stale_revision" : "credential_check_in_progress");
        }
        CredentialOAuthCheck? result = null;
        // The fence commit itself can outlast the claim. Never begin provider I/O using time reserved before that commit.
        duration = run.Claim.LeaseExpiresAtUtc - time.GetUtcNow() - TimeSpan.FromSeconds(1);
        if (duration > TimeSpan.FromSeconds(30)) duration = TimeSpan.FromSeconds(30);
        if (duration > TimeSpan.Zero)
        using (var deadline = new CancellationTokenSource(duration, time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token))
        {
            Task<CredentialOAuthCheck>? pending = null;
            try
            {
                if (!linked.IsCancellationRequested)
                {
                    pending = verifier.CheckAsync(bound!.Credential.Value, linked.Token).AsTask();
                    result = await pending.WaitAsync(linked.Token).ConfigureAwait(false);
                    if (linked.IsCancellationRequested || verifier.Describe(bound.Credential.Value.Purpose) != descriptor) result = null;
                }
            }
            catch (Exception) { if (pending is not null) _ = Observe(pending); } // Never repeat a possibly dispatched refresh or expose provider failures.
        }
        // Even shutdown/caller cancellation must leave a durable uncertain result when storage is available.
        using var settleDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
        var receipt = await Settle(dispatch, result, false, settleDeadline.Token).ConfigureAwait(false)
            ?? throw new SecurityConflictException("credential_check_unsettled");
        ct.ThrowIfCancellationRequested(); return receipt;
    }

    public async ValueTask<int> RecoverAsync(CancellationToken ct = default)
    {
        var count = 0;
        foreach (var dispatch in await checks.RecoverableCredentialChecksAsync(time.GetUtcNow(), 100, ct).ConfigureAwait(false))
            if (await Settle(dispatch, null, true, ct).ConfigureAwait(false) is not null) count++;
        return count;
    }
    private async ValueTask<TenantOperationReceipt?> Settle(TenantCredentialCheckDispatch dispatch, CredentialOAuthCheck? result, bool recovery, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var state = await security.ReadTenantAsync(dispatch.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
            var audit = new AuthorizationAudit(ids.NewId(), dispatch.TenantId, dispatch.ExecutorPrincipalId, TenantCredentialCheckRules.Action,
                dispatch.CredentialId.ToString("D"), result is null ? "denied" : "allowed", result is null ? "credential_check_uncertain" : "credential_connection_checked", time.GetUtcNow());
            var receipt = await checks.TrySettleCredentialCheckAsync(Next(state), state.Tenant.Revision, dispatch, result, audit, recovery, ct).ConfigureAwait(false);
            if (receipt is not null) return receipt;
        }
        return null;
    }
    private static TenantSecurityState Next(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
    private static async Task Observe(Task pending) { try { await pending.ConfigureAwait(false); } catch (Exception) { } }
    private static TenantOperationReceipt Matching(TenantOperationReceipt receipt, TenantApprovalRequest request, AgentRun run)
    {
        if (receipt.OperationId != request.Operation.OperationId || receipt.RequestId != request.RequestId || receipt.TenantId != request.TenantId
            || receipt.RunId != run.AgentRunId || receipt.SessionId != run.SessionId || receipt.RootActivationId != request.RootActivationId
            || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId || receipt.InstanceId != request.Operation.InstanceId || receipt.ActionHash != request.Operation.ActionHash)
            throw new SecurityConflictException("operation_mismatch");
        return receipt;
    }
}
