using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantWorkspaceOperationStore
{
    public ValueTask<TenantWorkspaceEffectStart?> TryStartWorkspaceEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantWorkspaceEffectCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var run = sessionBacking.ReadTenantRun(next.Tenant.TenantId, commit.RunId) ?? throw new AccessDeniedException("run_unavailable");
            var old = _operationReceipts.Values.SingleOrDefault(r => r.TenantId == next.Tenant.TenantId && r.RunId == commit.RunId && r.SourceCallId == commit.Operation.SourceCallId);
            if (old is not null) return ValueTask.FromResult<TenantWorkspaceEffectStart?>(new(null, TenantWorkspaceEffectRules.Match(old, run, commit.Operation.SourceCallId, commit.Operation.ParametersHash)));
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision || run.Revision != commit.ExpectedRunRevision) return ValueTask.FromResult<TenantWorkspaceEffectStart?>(null);
            var agent = FindTenantAgentAsync(before.Tenant.TenantId, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
            TenantSessionRules.RequireExecutor(before, agent); ValidateAgentActor(before, agent.PrincipalId);
            if (run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is { } human) ValidateAgentActor(before, human);
            RequireTenantExecutionSourcesAsync(run, before, new IamAuthorizer(this), this, ct).AsTask().GetAwaiter().GetResult();
            var session = sessionBacking.ReadTenantSession(before.Tenant.TenantId, run.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            TenantWorkspaceEffectRules.Session(run, agent, session);
            var definition = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
            var at = ApprovalNow; var args = TenantWorkspaceEffectRules.Validate(run, agent, definition, commit.Operation, commit.Generation, at);
            if (commit.ActionHash != TenantWorkspaceMutationContract.ActionHash(run, args.ParametersHash)) throw new SecurityConflictException("operation_mismatch");
            var correlated = RootApproval(before.Tenant.TenantId, run.Admission.Activation.RootActivationId, agent.PrincipalId,
                TenantWorkspaceMutationOperationRules.Correlation(run, args).OperationFingerprint);
            if (correlated is not null) throw new AccessDeniedException(TenantApprovalRules.DuplicateReason(correlated));
            TenantWorkspaceEffectRules.RequireAuthority(before, run, args, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            TenantWorkspaceEffectRules.Transaction(next, expectedSecurityRevision, run, audit, args, true);
            var saved = run.BeginDirectExternalEffect(run.Revision, commit.Generation, commit.Operation, commit.ActionHash, at);
            audit = TenantWorkspaceEffectRules.Audit(audit with { At = at, Decision = "allowed", ReasonCode = "workspace_mutation_dispatched" }, before, run, args, commit.Operation.OperationId, true);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct); ct.ThrowIfCancellationRequested();
            sessionBacking.SaveTenantOperationRun(saved, run.Revision); _tenants[next.Tenant.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantWorkspaceEffectStart?>(new(TenantWorkspaceEffectRules.Fence(saved), null));
        }
    }
    public ValueTask<IReadOnlyList<TenantWorkspaceDispatch>> RecoverableWorkspaceEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var result = new List<TenantWorkspaceDispatch>();
            foreach (var run in sessionBacking.AgentRuns.Runs.Values.Where(r => r.Owner.TenantId is not null && r.SideEffect.ExternalEffect?.DirectOperation is not null
                && r.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate).OrderBy(r => r.UpdatedAtUtc).ThenBy(r => r.AgentRunId))
            {
                var op = run.SideEffect.ExternalEffect!.DirectOperation!;
                if (_operationReceipts.ContainsKey(op.OperationId) || !TenantWorkspaceMutationContract.IsMutation(op.ToolName)
                    || sessionBacking.ReadRetainedTenantOperationRun(run.Owner.TenantId!.Value, run.AgentRunId) is null || !TenantWorkspaceEffectRules.Recoverable(run, at)) continue;
                result.Add(TenantWorkspaceEffectRules.Fence(run)); if (result.Count >= Math.Clamp(limit, 1, 100)) break;
            }
            return ValueTask.FromResult<IReadOnlyList<TenantWorkspaceDispatch>>(result);
        }
    }
    public ValueTask<TenantOperationReceipt?> TrySettleWorkspaceEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantWorkspaceDispatch fence, TenantOperationOutcome outcome, string reason, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default, TenantPublicDownloadReceipt? download = null)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next); TenantWorkspaceEffectRules.Outcome(outcome, reason, recovery);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var op = fence.Origin.DirectOperation ?? throw new SecurityConflictException("operation_mismatch");
            var run = sessionBacking.ReadRetainedTenantOperationRun(fence.TenantId, fence.RunId) ?? throw new AccessDeniedException("run_unavailable");
            if (_operationReceipts.GetValueOrDefault(op.OperationId) is { } old) return ValueTask.FromResult<TenantOperationReceipt?>(TenantWorkspaceEffectRules.Match(old, run, op.SourceCallId, op.ParametersHash));
            if (!_tenants.TryGetValue(fence.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            TenantWorkspaceEffectRules.RequireFence(run, fence); var at = ApprovalNow;
            if (at < fence.Origin.StartedAtUtc || recovery && !TenantWorkspaceEffectRules.Recoverable(run, at)) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var args = TenantWorkspaceMutationContract.Parse(run.SessionId, op.ToolName, op.ArgumentsJson);
            TenantWorkspaceEffectRules.Transaction(next, expectedSecurityRevision, run, audit, args, false);
            var saved = run.Status == AgentRunStatus.Running && !run.CancellationRequested && run.Claim?.Generation == fence.Origin.Generation
                && run.SideEffect.Disposition == AgentRunSideEffectDisposition.InFlight ? run.MarkSideEffect(run.Revision, fence.Origin.Generation,
                    outcome == TenantOperationOutcome.Completed ? AgentRunSideEffectDisposition.Succeeded : outcome == TenantOperationOutcome.Denied ? AgentRunSideEffectDisposition.DefinitelyFailed : AgentRunSideEffectDisposition.Indeterminate,
                    run.SideEffect.ToolCallId!, fence.ActionHash, at) : run;
            var receipt = TenantPublicDownloadRules.Attach(TenantWorkspaceEffectRules.Receipt(run, fence, outcome, reason, at), args, download, recovery);
            audit = TenantWorkspaceEffectRules.Audit(audit with { At = at, Decision = outcome == TenantOperationOutcome.Completed ? "allowed" : outcome == TenantOperationOutcome.Denied ? "denied" : "indeterminate", ReasonCode = reason }, before, run, args, op.OperationId, false);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct); ct.ThrowIfCancellationRequested();
            if (saved.Revision != run.Revision) sessionBacking.SaveRetainedTenantOperationRun(saved, run.Revision);
            _operationReceipts.Add(receipt.OperationId, receipt); _tenants[fence.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantOperationReceipt?>(receipt);
        }
    }
}
