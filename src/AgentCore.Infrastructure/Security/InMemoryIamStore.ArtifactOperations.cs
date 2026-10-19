using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantArtifactOperationStore
{
    public ValueTask<TenantOperationReceipt?> TryCommitArtifactAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantArtifactCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var run = sessionBacking.ReadTenantRun(next.Tenant.TenantId, commit.RunId) ?? throw new AccessDeniedException("run_unavailable");
            var existing = _operationReceipts.Values.SingleOrDefault(r => r.TenantId == next.Tenant.TenantId && r.RunId == commit.RunId && r.SourceCallId == commit.SourceCallId);
            if (existing is not null) return ValueTask.FromResult<TenantOperationReceipt?>(TenantArtifactOperationRules.Match(existing, run, commit.SourceCallId, commit.ParametersHash));
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision || run.Revision != commit.ExpectedRunRevision)
                return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var target = FindTenantAgentAsync(before.Tenant.TenantId, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
            TenantSessionRules.RequireExecutor(before, target); ValidateAgentActor(before, target.PrincipalId);
            if (run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is { } requester) ValidateAgentActor(before, requester);
            var at = (clock ?? TimeProvider.System).GetUtcNow();
            var args = TenantArtifactOperationRules.Validate(run, target, commit, at);
            var publication = new PreparedArtifactPublication(run.SessionId, args.CanonicalArguments, commit.Source);
            var prepared = TenantApprovalRules.PrepareArtifact(commit.OperationId, run.AgentInstanceId, publication, target.Instance.Revision, target.Instance.PersonaRevision);
            var correlated = RootApproval(before.Tenant.TenantId, run.Admission.Activation.RootActivationId, target.PrincipalId, prepared.OperationFingerprint);
            if (correlated is not null) throw new AccessDeniedException(TenantApprovalRules.DuplicateReason(correlated));
            var definition = TenantAgentDefinitionBaseline(target);
            if (!AgentCore.Application.Tools.TenantArtifactToolContract.AllowsPublication(definition, publication)) throw new AccessDeniedException("capability_unavailable");
            TenantArtifactOperationRules.RequireAuthorityAsync(before, run, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            if (args.SourcePath is { } path)
            {
                var session = sessionBacking.ReadTenantSession(before.Tenant.TenantId, run.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
                TenantArtifactSourceRules.Require(run, target, definition, session, path, commit.Source);
                if (commit.Source?.ResourceKey is { } sourceKey) TenantResourceRevisionRules.Require(sourceKey, commit.Source.ResourceRevision, run, target,
                    instances.ReadTenantResourcesAsync(target.Instance.InstanceId, ct).Result);
                TenantArtifactSourceRules.RequireAuthorityAsync(before, run, path, new IamAuthorizer(this), false, ct).GetAwaiter().GetResult();
            }
            TenantArtifactOperationRules.Transaction(next, expectedSecurityRevision, run, audit);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct);
            audit = TenantOperationAuditRules.WithLocalEffectResult(audit, before, run, commit.OperationId);
            var materialized = TenantArtifactSourceRules.MaterializeAsync(run, publication, commit.SourceContent, ct).GetAwaiter().GetResult();
            at = (clock ?? TimeProvider.System).GetUtcNow(); _ = TenantArtifactOperationRules.Validate(run, target, commit, at);
            var savedRun = run.RecordDirectLocalEffect(run.Revision, commit.Generation, commit.OperationId, commit.ActionHash, at);
            var receipt = new TenantOperationReceipt(commit.OperationId, null, before.Tenant.TenantId, run.AgentRunId, run.SessionId,
                run.Admission.ExecutionContext.RootActivationId, target.PrincipalId, run.AgentInstanceId, commit.ActionHash, TenantOperationOutcome.Completed,
                "operation_completed", target.Instance.Revision, target.Instance.PersonaRevision, DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()), commit.SourceCallId, commit.ParametersHash);
            if (_operationReceipts.ContainsKey(receipt.OperationId)) throw new SecurityConflictException("identity_conflict");
            var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, commit.Generation); var bytes = materialized.Bytes;
            var artifact = TenantArtifactRules.Record(frame, commit.OperationId, materialized.DisplayName, materialized.ContentType, bytes, at);
            ct.ThrowIfCancellationRequested();
            (artifactBacking ?? throw new AccessDeniedException("tenant_effect_unavailable")).PublishTenantLocal(frame, artifact, bytes, false, ct);
            sessionBacking.SaveTenantOperationRun(savedRun, run.Revision);
            _operationReceipts.Add(receipt.OperationId, receipt); _tenants[before.Tenant.TenantId] = next; _audit.Add(audit with { At = at });
            return ValueTask.FromResult<TenantOperationReceipt?>(receipt);
        }
    }
}
