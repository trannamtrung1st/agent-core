using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantOperationReceipt?> TryCommitManagementCommandAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantManagementCommandCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (isolationBacking.IsolationGate)
        return ValueTask.FromResult(WithTenantEvents(() => {
            var run = sessionBacking.ReadTenantRun(next.Tenant.TenantId, commit.RunId) ?? throw new AccessDeniedException("run_unavailable");
            var saved = _operationReceipts.Values.SingleOrDefault(r => r.TenantId == next.Tenant.TenantId && r.RunId == commit.RunId && r.SourceCallId == commit.SourceCallId);
            if (saved is not null) return TenantLocalEffectRules.MatchDirectReceipt(saved, run, commit.SourceCallId, commit.ParametersHash);
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision || run.Revision != commit.ExpectedRunRevision) return null;
            var reader = ManagementReader(run, null, ct); var value = commit.Operation.ManagementMutation ?? throw new SecurityConflictException("operation_mismatch");
            ValidateAgentActor(before, reader.Agent.PrincipalId);
            if (run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is { } human) ValidateAgentActor(before, human);
            var at = (clock ?? TimeProvider.System).GetUtcNow();
            TenantManagementDirectRules.Transaction(next, expectedSecurityRevision, reader, commit, audit, at);
            if (value.Skill is not null) RequireManagementSkillSources(reader, TenantManagementMutationContract.Parse(value.Harness?.ToolName ?? value.ToolName, value.Harness?.ArgumentsJson ?? value.ArgumentsJson), null, ct);
            TenantManagementDirectRules.AuthorityAsync(reader, value, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
            var previous = RootApproval(before.Tenant.TenantId, run.Admission.Activation.RootActivationId, reader.Agent.PrincipalId, commit.Operation.OperationFingerprint);
            if (previous is not null) throw new AccessDeniedException(TenantApprovalRules.DuplicateReason(previous));
            var source = ReadTenantAgentConfigurationAsync(before.Tenant.TenantId, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("management_unavailable");
            TenantAgentConfigurationChange change;
            if (value.Resource is { } resource)
            {
                var context = ManagementResourceContext(reader, TenantManagementMutationContract.Parse(value.Harness?.ToolName ?? value.ToolName, value.Harness?.ArgumentsJson ?? value.ArgumentsJson), null, ct);
                if (System.Text.Json.JsonSerializer.Serialize(context.Material) != System.Text.Json.JsonSerializer.Serialize(resource.Source)) throw new AccessDeniedException("resource_source_changed");
                if (resource.InstanceResource is { } binding) _ = AgentCore.Application.Admin.DefinitionResourceStoredContent.RequireForBindingAsync(definitions.ResourceStore!.TenantContent,
                    binding.ContentSha256, binding.ByteLength, binding.MediaType, ct).GetAwaiter().GetResult();
                change = TenantManagementMutationRules.Change(reader, value, context.Source.Snapshot, context.Published);
            }
            else change = TenantManagementMutationRules.Change(reader, value, source);
            var candidate = TenantAgentConfigurationRules.Apply(reader.Agent.Instance, change, at);
            var reconcileSpaces = PrepareResourceSpaceReconciliation(before.Tenant.TenantId, change, audit, ct);
            next = TenantAgentChildScopes.RetainAvailable(next, run.AgentInstanceId, ChildCatalog(before.Tenant.TenantId, run.AgentInstanceId, change, ct));
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct);
            var updatedRun = run.RecordDirectLocalEffect(run.Revision, commit.Generation, commit.Operation.OperationId, commit.Operation.ActionHash, at);
            var receipt = new TenantOperationReceipt(commit.Operation.OperationId, null, before.Tenant.TenantId, run.AgentRunId, run.SessionId,
                run.Admission.Activation.RootActivationId, reader.Agent.PrincipalId, run.AgentInstanceId, commit.Operation.ActionHash,
                TenantOperationOutcome.Completed, TenantManagementMutationRules.Completed, candidate.Revision, candidate.PersonaRevision,
                DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()), commit.SourceCallId, commit.ParametersHash, ManagementSkill: value.Skill is null ? null : TenantManagementSkillRules.Evidence(value, reader.Agent.Instance.Revision),
                ManagementResource: value.Resource is null ? null : TenantManagementResourceRules.Evidence(value, reader.Agent.Instance.Revision));
            if (_operationReceipts.ContainsKey(receipt.OperationId)) throw new SecurityConflictException("identity_conflict");
            TenantLocalEffectRules.RequireDirectRun(run, commit.Generation, (clock ?? TimeProvider.System).GetUtcNow());
            ct.ThrowIfCancellationRequested();
            if (change.Resource is { } resourceChange) instances.MutateTenantResourcesAsync(resourceChange with { HarnessManagement = value.Harness is null ? null : candidate.HarnessManagement }, at, ct).GetAwaiter().GetResult();
            else if (change.Skill is { } skill) instances.MutateTenantSkillsAsync(skill, at, ct).GetAwaiter().GetResult();
            else _ = instances.ChangeTenantConfigurationAsync(new(run.AgentInstanceId, reader.Agent.Instance.Revision, HarnessManagement: change.HarnessManagement), at, ct).Result;
            foreach (var fact in AgentCore.Infrastructure.Persistence.CoreEventPersistence.Instance(reader.Agent.Instance, candidate,
                AgentCore.Domain.Triggers.TriggerOwner.ForTenant(before.Tenant.TenantId, run.AgentInstanceId, reader.Agent.PrincipalId))) AppendTenantCore(fact);
            sessionBacking.SaveTenantOperationRun(updatedRun, run.Revision);
            reconcileSpaces();
            _operationReceipts.Add(receipt.OperationId, receipt); _tenants[before.Tenant.TenantId] = next;
            _audit.Add(TenantOperationAuditRules.WithLocalEffectResult(audit, before, run, receipt.OperationId) with { At = at });
            return receipt;
        }));
    }
}
