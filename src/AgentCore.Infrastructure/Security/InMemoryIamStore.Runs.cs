using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantSessionRoot?> ResolveTenantSessionOwnerAsync(Guid sessionId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(sessionBacking.ResolveTenantSessionOwner(sessionId)); }
    public ValueTask<AgentRun?> TryCancelTenantRunAsync(TenantSecurityState next, long expectedSecurityRevision, Guid runId, long expectedRunRevision,
        TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, ConversationEntry? response = null)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        if (next.Tenant.Revision != checked(expectedSecurityRevision + 1) || audit.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != initiator.PrincipalId
            || audit.Action != "agent-runs.cancel" || audit.Target != runId.ToString("D")) throw new ArgumentException("Invalid Run cancellation transaction.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<AgentRun?>(null);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, initiator.PrincipalId); EnsureReferences(next, audit, ct: ct);
            RequireHumanSession(next.Tenant.TenantId, initiator); ct.ThrowIfCancellationRequested();
            var run = sessionBacking.ReadTenantRun(next.Tenant.TenantId, runId);
            if (run is null || run.Revision != expectedRunRevision) return ValueTask.FromResult<AgentRun?>(null);
            var request = run.ApprovalRequestId is { } id ? _approvals.GetValueOrDefault(id) : null;
            var source = request is not null && request.RunId != runId ? sessionBacking.ReadTenantRun(request.TenantId, request.RunId) : null;
            var closed = TenantRunCancellationRules.Approval(request, run, source, at);
            var saved = sessionBacking.CancelTenantRun(next.Tenant.TenantId, runId, expectedRunRevision, at, response);
            if (saved is not null)
            {
                SettleTenantCompletion(saved, run.Claim?.Generation);
                if (closed is not null && closed != request)
                {
                    _approvals[closed.RequestId] = closed;
                    if (closed.Status == TenantApprovalStatus.Cancelled)
                        _approvalDecisions.Add(closed.RequestId, new(audit.AuditId, closed.RequestId, closed.TenantId, initiator.PrincipalId,
                            closed.Status, closed.Operation.ActionHash, closed.Revision, at));
                }
                if (run.ApprovalRequestId is { } approvalId && _operationGrants.GetValueOrDefault(approvalId) is { Status: OperationGrantStatus.Available } grant)
                    _operationGrants[approvalId] = grant with { Status = OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) };
                _tenants[next.Tenant.TenantId] = next; _audit.Add(audit);
            }
            return ValueTask.FromResult(saved);
        }
    }
    public ValueTask<AgentRun?> FindTenantRunAsync(Guid tenantId, Guid runId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(sessionBacking.ReadTenantRun(tenantId, runId)); }
    public ValueTask<IReadOnlyList<Guid>> ListTenantRunIdsAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(sessionBacking.TenantRunIds(tenantId, sessionId)); }
    public async ValueTask<TenantRunAdmissionResult?> TryAdmitTenantHumanRunAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantHumanRunAdmission admission, TenantSessionInitiator initiator, AuthorizationAudit audit, CancellationToken ct = default)
    {
        await using var speechOperation = admission.Speech is { } speech ? await speech.Lease.EnterAsync(requireActive: true, ct).ConfigureAwait(false) : null;
        return await CommitTenantHumanRun(next, expectedSecurityRevision, admission, initiator, audit, ct, speechOperation: speechOperation).ConfigureAwait(false);
    }
    private ValueTask<TenantRunAdmissionResult?> CommitTenantHumanRun(TenantSecurityState next, long expectedSecurityRevision,
        TenantHumanRunAdmission admission, TenantSessionInitiator initiator, AuthorizationAudit audit, CancellationToken ct,
        TenantRuntimeFrame? frame = null, SessionSnapshot? liveSnapshot = null, long runtimeExpectedRevision = 0, TenantVoiceLease.Operation? speechOperation = null)
    {
        ct.ThrowIfCancellationRequested(); admission = TenantAttachmentBindingRules.Freeze(admission); next = IamStateRules.Freeze(next); var run = admission.Run;
        if (run.Owner.TenantId != next.Tenant.TenantId || next.Tenant.Revision != checked(expectedSecurityRevision + 1)
            || audit.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != initiator.PrincipalId
            || audit.Action != "agent-runs.admit" || audit.Target != run.AgentRunId.ToString("D")) throw new ArgumentException("Invalid Run admission transaction.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (attachmentBacking?.TenantGate ?? _gate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantRunAdmissionResult?>(null);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
            ValidateAgentActor(before, initiator.PrincipalId); EnsureReferences(next, audit, ct: ct); RequireHumanSession(next.Tenant.TenantId, initiator);
            var agent = FindTenantAgentAsync(next.Tenant.TenantId, run.AgentInstanceId, ct).Result;
            if (agent is null || agent.Instance.Revision != admission.ExpectedInstanceRevision) return ValueTask.FromResult<TenantRunAdmissionResult?>(null);
            if (agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
            TenantSessionRules.RequireExecutor(before, agent); ct.ThrowIfCancellationRequested();
            RequireSpaceExecutionSource(run, before, new IamAuthorizer(this), ct, admission: true).AsTask().GetAwaiter().GetResult();
            var speechSession = sessionBacking.ReadTenantSession(next.Tenant.TenantId, run.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            TenantVoiceRules.Speech(admission, initiator, speechSession.Owner, speechSession.Snapshot.Mode, speechOperation);
            TenantInputInterruptionRules.Shape(admission);
            TenantInputInterruptionRules.PermissionsAsync(before, initiator.PrincipalId, admission.Interruption, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            var sources = admission.Attachments ?? [];
            if (sources.Count > 0 && attachmentBacking is null) throw new AccessDeniedException("attachment_unavailable");
            TenantAttachmentBindingRules.PermissionsAsync(before, initiator.PrincipalId, run.SessionId, sources, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            var result = sessionBacking.AdmitTenantRun(admission, agent, initiator, () => {
                var actual = sources.Select(source => attachmentBacking!.RetainedTenant(next.Tenant.TenantId, run.SessionId, source.AttachmentId)
                    ?? throw new AccessDeniedException("attachment_unavailable")).ToArray();
                TenantAttachmentBindingRules.Current(admission, actual, (clock ?? TimeProvider.System).GetUtcNow());
            }, frame, liveSnapshot, runtimeExpectedRevision, clock ?? TimeProvider.System);
            if (result?.Created == true) attachmentBacking?.BindTenantSources(admission);
            if (result?.Created == true)
            {
                if (result.Interrupted is { } stopped)
                {
                    SettleTenantCompletion(stopped, frame!.Generation);
                    if (stopped.ApprovalRequestId is { } requestId && _operationGrants.GetValueOrDefault(requestId) is { Status: OperationGrantStatus.Available } grant)
                        _operationGrants[requestId] = grant with { Status = OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) };
                    _audit.Add(TenantInputInterruptionRules.Audit(admission, initiator, stopped.UpdatedAtUtc));
                }
                _tenants[next.Tenant.TenantId] = next; _audit.Add(audit);
            }
            return ValueTask.FromResult(result);
        }
    }
}
