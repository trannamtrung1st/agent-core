using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantOperationReceipt?> TryCommitTenantExperienceAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantExperienceCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (isolationBacking.IsolationGate)
        {
            var run = sessionBacking.ReadTenantRun(next.Tenant.TenantId, commit.RunId) ?? throw new AccessDeniedException("run_unavailable");
            var existing = _operationReceipts.Values.SingleOrDefault(r => r.TenantId == next.Tenant.TenantId && r.RunId == commit.RunId && r.SourceCallId == commit.SourceCallId);
            if (existing is not null) return ValueTask.FromResult<TenantOperationReceipt?>(TenantLocalEffectRules.MatchDirectReceipt(existing, run, commit.SourceCallId, commit.ParametersHash));
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision || run.Revision != commit.ExpectedRunRevision) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var observed = ObserveExperienceOperation(run, TenantExperienceToolContract.Parse(commit.Prepared.ToolName, commit.Prepared.ArgumentsJson), commit.Prepared, null, ct);
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            var at = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
            var prepared = TenantExperienceCommitRules.Require(observed, commit, now); var iam = new IamAuthorizer(this);
            TenantExperienceRules.RawAsync(observed.Reader, observed.Source!.Witness, false, iam, ct, manage: true).AsTask().GetAwaiter().GetResult();
            if (TenantSandboxReadOperationRules.NeedsReviewAsync(before, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, "memory.manage", AccessScope.ForResource(before.Tenant.TenantId, "agent", run.AgentInstanceId.ToString("D")), iam, ct).AsTask().GetAwaiter().GetResult()) throw new AccessDeniedException("experience_review_required");
            var previous = RootApproval(before.Tenant.TenantId, run.Admission.Activation.RootActivationId, observed.Reader.Agent.PrincipalId, prepared.OperationFingerprint);
            if (previous is not null) throw new AccessDeniedException(TenantApprovalRules.DuplicateReason(previous));
            TenantExperienceCommitRules.Transaction(next, expectedSecurityRevision, run, audit);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct);
            var values = TenantExperienceOperationRules.Values(observed, at);
            if (values.Count > 0 && tenantExperienceIndex.GetValueOrDefault((before.Tenant.TenantId, run.AgentInstanceId, values[^1].OwnerKey))?.Count(id => tenantExperiences[(before.Tenant.TenantId, id)].Visibility != ExperienceVisibility.Deleted) >= TenantExperienceRules.MaxRetained)
                throw new AccessDeniedException("experience_capacity");
            var savedRun = run.RecordDirectLocalEffect(run.Revision, commit.Generation, commit.OperationId, commit.ActionHash, (clock ?? TimeProvider.System).GetUtcNow());
            var receipt = new TenantOperationReceipt(commit.OperationId, null, before.Tenant.TenantId, run.AgentRunId, run.SessionId, run.Admission.ExecutionContext!.RootActivationId,
                observed.Reader.Agent.PrincipalId, run.AgentInstanceId, commit.ActionHash, TenantOperationOutcome.Completed, TenantExperienceRules.Recorded,
                observed.Reader.Agent.Instance.Revision, observed.Reader.Agent.Instance.PersonaRevision, at, commit.SourceCallId, commit.ParametersHash);
            if (_operationReceipts.ContainsKey(receipt.OperationId)) throw new SecurityConflictException("identity_conflict");
            ct.ThrowIfCancellationRequested();
            foreach (var value in values) SaveExperience(value);
            sessionBacking.SaveTenantOperationRun(savedRun, run.Revision); _operationReceipts.Add(receipt.OperationId, receipt);
            _tenants[before.Tenant.TenantId] = next; _audit.Add(TenantOperationAuditRules.WithLocalEffectResult(audit, before, run, commit.OperationId) with { At = at });
            return ValueTask.FromResult<TenantOperationReceipt?>(receipt);
        }
    }
    private IReadOnlyList<AgentCore.Domain.Experience.TenantExperience> RequireExperienceEffect(TenantApprovalRequest request, AgentCore.Domain.Conversation.AgentRun run, Guid human, DateTimeOffset at, CancellationToken ct)
    {
        var value = request.Operation.ExperienceOperation!;
        var observed = ObserveExperienceOperation(run, TenantExperienceToolContract.Parse(value.ToolName, value.ArgumentsJson), value, null, ct);
        TenantExperienceConsentRules.ExecutorAsync(request, observed, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
        TenantExperienceConsentRules.ReviewerAsync(request, observed, human, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
        var values = TenantExperienceOperationRules.Values(observed, DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()));
        if (values.Count > 0)
        {
            var owner = values[^1].OwnerKey;
            var retained = tenantExperienceIndex.GetValueOrDefault((request.TenantId, run.AgentInstanceId, owner))?.Count(id => tenantExperiences[(request.TenantId, id)].Visibility != ExperienceVisibility.Deleted) ?? 0;
            if (retained >= TenantExperienceRules.MaxRetained) throw new AccessDeniedException("experience_capacity");
        }
        return values;
    }

    private TenantExperienceOperationObservation RequireExperienceApproval(TenantApprovalRequest request, AgentCore.Domain.Conversation.AgentRun run, CancellationToken ct)
    {
        var value = request.Operation.ExperienceOperation!;
        var observed = ObserveExperienceOperation(run, TenantExperienceToolContract.Parse(value.ToolName, value.ArgumentsJson), value, null, ct);
        TenantExperienceConsentRules.ExecutorAsync(request, observed, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
        return observed;
    }

}
