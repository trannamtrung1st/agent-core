using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private IReadOnlyList<ArtifactRecord> RequireCompletionReadOperation(TenantApprovalRequest request, AgentRun parent, TenantAgent agent)
    {
        var read = request.Operation.CompletionRead!;
        var own = sessionBacking.ReadTenantSession(request.TenantId, parent.SessionId) ?? throw new AccessDeniedException("session_unavailable");
        TenantWaitRules.RequireSession(own);
        var child = TenantWaitRules.ChildAsync(parent, sessionBacking.ReadTenantSession(request.TenantId, read.ChildSessionId),
            (id, _) => ValueTask.FromResult(sessionBacking.ReadRetainedTenantOperationRun(request.TenantId, id)), CancellationToken.None).AsTask().GetAwaiter().GetResult()
            ?? throw new AccessDeniedException("completion_source_changed");
        var files = new List<ArtifactRecord>();
        foreach (var expected in TenantCompletionReadRules.Artifacts(read))
        {
            var item = artifactBacking?.FindTenantAsync(request.TenantId, read.ChildSessionId, expected.ArtifactId).Result;
            if (item is null || item.AgentRunId != read.SourceRunId || TenantArtifactVerificationRules.Witness(item) != expected)
                throw new AccessDeniedException("completion_source_changed");
            files.Add(item);
        }
        var current = InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
        TenantCompletionReadRules.Require(read, parent, agent, current, child);
        if (!TenantCompletionReadRules.Same(read, TenantCompletionReadRules.Witness(parent, child, files))) throw new AccessDeniedException("completion_source_changed");
        TenantCompletionReadRules.PermissionsAsync(_tenants[request.TenantId], read, agent.PrincipalId, new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
        return files;
    }
    private BackgroundCompletionCandidate? AuthorizeCompletionSource(AgentRun parent, BackgroundCompletionCandidate child, CancellationToken ct)
    {
        var state = _tenants[parent.Owner.TenantId!.Value]; var source = child.CurrentTaskRun ?? child.Run;
        var request = source.IsTerminal && source.Result?.OutcomeKind != AgentRunOutcomeKind.Continued
            ? _approvals.Values.SingleOrDefault(r => r.TenantId == state.Tenant.TenantId && r.RootActivationId == parent.Admission.ExecutionContext!.RootActivationId
                && r.ExecutorPrincipalId == parent.Owner.ExecutorPrincipalId && r.Operation.CompletionRead is { } read
                && read.ParentSessionId == parent.SessionId && read.ChildSessionId == source.SessionId && read.InitialRunId == child.Run.AgentRunId) : null;
        if (request is null) return TenantWaitRules.CanReadAsync(state, parent, child, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult() ? child : null;
        if (!TenantCompletionReadReferences.Contains(parent, source.SessionId, source.AgentRunId, request.RequestId)
            || !_operationReceipts.TryGetValue(request.Operation.OperationId, out var receipt) || !_approvalDecisions.TryGetValue(request.RequestId, out var decision)
            || _operationGrants.ContainsKey(request.RequestId)) return null;
        try
        {
            lock (instances.CredentialGate) lock (definitions.TenantGate)
            {
                var agent = FindTenantAgentAsync(state.Tenant.TenantId, parent.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
                if (agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision)
                    throw new AccessDeniedException("operation_witness_changed");
                var files = RequireCompletionReadOperation(request, parent, agent);
                TenantCompletionReadService.RequireApprovedAsync(parent, state, request.Operation.CompletionRead!, request, receipt, decision, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
                return child with { CompletionReadRequestId = request.RequestId, ReviewedArtifacts = files };
            }
        }
        catch (AccessDeniedException) { return null; }
        catch (SecurityConflictException) { return null; }
    }

    private ValueTask<PreparedCompletionRead> RequireCompletionSourceProof(AgentRun parent, Guid session, Guid source, Guid request, CancellationToken ct)
    {
        var tenant = parent.Owner.TenantId!.Value;
        var child = TenantWaitRules.ChildAsync(parent, sessionBacking.ReadTenantSession(tenant, session),
            (id, _) => ValueTask.FromResult(sessionBacking.ReadRetainedTenantOperationRun(tenant, id)), ct).AsTask().GetAwaiter().GetResult();
        var allowed = child is null ? null : AuthorizeCompletionSource(parent, child, ct);
        if (allowed is null || allowed.CompletionReadRequestId != request || (allowed.CurrentTaskRun ?? allowed.Run).AgentRunId != source)
            throw new AccessDeniedException("completion_source_unavailable");
        return ValueTask.FromResult(TenantCompletionReadRules.Witness(parent, allowed, allowed.ReviewedArtifacts!));
    }

}
