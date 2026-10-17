using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantPreferenceOperationStore
{
    public ValueTask<TenantOperationReceipt?> FindPreferenceReceiptAsync(Guid tenantId, Guid runId, string sourceCallId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_operationReceipts.Values.SingleOrDefault(r =>
            r.TenantId == tenantId && r.RunId == runId && r.SourceCallId == sourceCallId));
    }
    public ValueTask<TenantOperationReceipt?> TryCommitPreferencesAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantPreferenceCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            var run = sessionBacking.ReadTenantRun(next.Tenant.TenantId, commit.RunId) ?? throw new AccessDeniedException("run_unavailable");
            var existing = _operationReceipts.Values.SingleOrDefault(r => r.TenantId == next.Tenant.TenantId && r.RunId == commit.RunId && r.SourceCallId == commit.SourceCallId);
            if (existing is not null) return ValueTask.FromResult<TenantOperationReceipt?>(TenantPreferenceOperationRules.Match(existing, run, commit.SourceCallId, commit.ParametersHash));
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision || run.Revision != commit.ExpectedRunRevision)
                return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var target = FindTenantAgentAsync(before.Tenant.TenantId, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
            TenantSessionRules.RequireExecutor(before, target); ValidateAgentActor(before, target.PrincipalId);
            if (run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is { } requester) ValidateAgentActor(before, requester);
            var at = (clock ?? TimeProvider.System).GetUtcNow();
            var candidate = TenantPreferenceOperationRules.Apply(run, target, commit, at);
            TenantPreferenceOperationRules.Transaction(next, expectedSecurityRevision, run, audit);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct);
            at = (clock ?? TimeProvider.System).GetUtcNow();
            _ = TenantPreferenceOperationRules.Apply(run, target, commit, at);
            var savedRun = run.RecordDirectLocalEffect(run.Revision, commit.Generation, commit.OperationId, commit.ActionHash, at);
            var receipt = new TenantOperationReceipt(commit.OperationId, null, before.Tenant.TenantId, run.AgentRunId, run.SessionId,
                run.Admission.ExecutionContext.RootActivationId, target.PrincipalId, run.AgentInstanceId, commit.ActionHash, TenantOperationOutcome.Completed,
                "operation_completed", candidate.Revision, candidate.PersonaRevision, DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()), commit.SourceCallId, commit.ParametersHash);
            if (_operationReceipts.ContainsKey(receipt.OperationId)) throw new SecurityConflictException("identity_conflict");
            ct.ThrowIfCancellationRequested();
            _ = instances.ChangeTenantConfigurationAsync(new(run.AgentInstanceId, target.Instance.Revision,
                SetSettingsOverrides: true, SettingsOverrides: candidate.SettingsOverrides, AutomationPolicyChanges: []), at, ct).Result;
            sessionBacking.SaveTenantOperationRun(savedRun, run.Revision);
            _operationReceipts.Add(receipt.OperationId, receipt); _tenants[before.Tenant.TenantId] = next; _audit.Add(audit with { At = at });
            return ValueTask.FromResult<TenantOperationReceipt?>(receipt);
        }
    }
}
