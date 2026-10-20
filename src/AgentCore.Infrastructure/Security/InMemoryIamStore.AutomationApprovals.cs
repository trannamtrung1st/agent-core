using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<bool> IsTenantAutomationBusyAsync(Guid tenant, Guid automation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (automationBacking.AdmissionGate)
            return ValueTask.FromResult(automationBacking.ReadTenantAutomation(tenant, automation) is not null && automationBacking.TenantBusy(automation));
    }

    public ValueTask<TenantAutomationAdmissionResult?> TryPrepareTenantAutomationApprovalAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantAutomationAdmission admission, TenantApprovalRequest request, HumanSessionReference? human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (automationBacking.AdmissionGate) lock (webhookBacking.TenantGate) lock (sessionBacking.CoreEvents.TenantGate)
        {
            if (!_tenants.TryGetValue(request.TenantId, out var state) || state.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantAutomationAdmissionResult?>(null);
            var p = request.Operation.AutomationAdmission ?? throw new ArgumentException("Exact Automation consent source required.");
            if (human is not null) RequireHumanSession(request.TenantId, human);
            if (human?.PrincipalId != p.ManualInitiatorPrincipalId) throw new AccessDeniedException("initiator_unavailable");
            var current = automationBacking.ReadTenantAutomation(request.TenantId, p.Current.AutomationId);
            if (current is null || !TenantAutomationRules.Same(current, p.Current)) return ValueTask.FromResult<TenantAutomationAdmissionResult?>(null);
            var agent = FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
            var policy = InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
            ValidateAutomationPreparation(next, expectedSecurityRevision, request, audit);
            if (request.ExpiresAt <= ApprovalNow) throw new SecurityConflictException("approval_expired");
            TenantAutomationAdmissionApprovalRules.Shape(request, agent);
            TenantAutomationRules.Admission(admission, agent, human?.PrincipalId, p);
            var decision = TenantAutomationAdmissionApprovalRules.EvaluateAsync(state, current, agent, policy, human?.PrincipalId, new IamAuthorizer(this), ct, p.Occurrence.SourceKind, p.EventSource, p.EmailSourceAccountIds).Result;
            if (decision.Kind != request.Kind || decision.Permission != request.Permission) throw new SecurityConflictException("operation_authority_changed");
            if (human is not null) RequireAutomationHuman(state, human.PrincipalId, agent, current.AutomationId, "automations.run", false, ct);
            if (automationBacking.TenantBusy(current.AutomationId)) throw new SecurityConflictException("automation_in_use");
            if (_approvals.ContainsKey(request.RequestId) || _approvals.Values.Any(r => r.Operation.OperationId == request.Operation.OperationId)) throw new SecurityConflictException("identity_conflict");
            var recent = _approvals.Values.Where(r => r.TenantId == request.TenantId && (r.Status == TenantApprovalStatus.Pending || r.CreatedAt >= ApprovalNow - TenantApprovalLimits.Window)).ToArray();
            TenantApprovalLimits.Require(recent.Length, recent.Count(r => r.ExecutorPrincipalId == request.ExecutorPrincipalId),
                recent.Count(r => r.OriginalInitiatorPrincipalId == request.OriginalInitiatorPrincipalId), recent.Count(r => r.Fingerprint == request.Fingerprint),
                _approvals.Values.Count(r => r.TenantId == request.TenantId && r.RootActivationId == request.RootActivationId), request.OriginalInitiatorPrincipalId is not null);
            var qualified = state.Users.Any(user => {
                try { TenantAutomationAdmissionApprovalRules.ReviewerAsync(state, request, user.PrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult(); return true; }
                catch (AccessDeniedException) { return false; } });
            if (!qualified) throw new AccessDeniedException("no_eligible_approver");
            RequireTenantEventAdmission(admission, p, false, ct);
            EnsureReferences(next, audit, ct: ct); ct.ThrowIfCancellationRequested();
            var occurrence = TenantAutomationAdmissionApprovalRules.Pending(p);
            automationBacking.CommitTenantOccurrence(p.Next, occurrence); SettleTenantEventSource(p.EventSource, occurrence.OccurrenceId, true); _approvals.Add(request.RequestId, request); _tenants[request.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantAutomationAdmissionResult?>(new(true, occurrence, null, null, request.RequestId));
        }
    }

    private static void ValidateAutomationPreparation(TenantSecurityState next, long expected, TenantApprovalRequest request, AuthorizationAudit audit)
    {
        if (next.Tenant.TenantId != request.TenantId || next.Tenant.Revision != expected + 1 || request.Status != TenantApprovalStatus.Pending || request.Revision != 1
            || audit.TenantId != request.TenantId || audit.ActorPrincipalId != request.ExecutorPrincipalId || audit.Action != "approvals.request"
            || audit.Target != request.RequestId.ToString("D") || audit.At != request.CreatedAt || audit.Decision != "allowed") throw new ArgumentException("Invalid occurrence consent transaction.");
    }

    private TenantAgent RequireAutomationAdmissionOperation(TenantApprovalRequest request, TenantSecurityState state, CancellationToken ct)
    {
        var p = request.Operation.AutomationAdmission!;
        var current = automationBacking.ReadTenantAutomation(request.TenantId, p.Current.AutomationId) ?? throw new AccessDeniedException("automation_unavailable");
        var occurrence = automationBacking.ReadTenantOccurrence(request.TenantId, p.Occurrence.OccurrenceId) ?? throw new AccessDeniedException("occurrence_unavailable");
        if (!TenantAutomationRules.Same(current, p.Next) || !TenantAutomationAdmissionApprovalRules.SameOccurrence(occurrence, TenantAutomationAdmissionApprovalRules.Pending(p))) throw new SecurityConflictException("operation_witness_changed");
        var agent = FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
        TenantAutomationAdmissionApprovalRules.Shape(request, agent);
        var policy = InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
        try { TenantAutomationRules.AdmissionSource(state, p.Current, agent, policy, p.ManualInitiatorPrincipalId, p.Occurrence.SourceKind); }
        catch (ArgumentException) { throw new AccessDeniedException("operation_configuration_changed"); }
        if (p.EventSource is { } eventSource) RequireTenantEventSource(p.Current, p.Occurrence, eventSource, true, ct);
        TenantAutomationAdmissionApprovalRules.ExecutorAsync(state, request, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
        return agent;
    }

    public ValueTask<bool> TrySettleTenantAutomationApprovalAsync(long expectedSecurityRevision, ApprovalProcessedSignal signal,
        TenantAutomationAdmission? admission, string? refusal, Guid auditId, DateTimeOffset at, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (automationBacking.AdmissionGate) lock (webhookBacking.TenantGate) lock (sessionBacking.CoreEvents.TenantGate)
        {
            at = ApprovalNow;
            if (!_tenants.TryGetValue(signal.TenantId, out var state) || state.Tenant.Revision != expectedSecurityRevision
                || !_approvalSignals.TryGetValue(signal.SignalId, out var saved) || saved != signal || saved.ProcessedAt is not null
                || !_approvals.TryGetValue(signal.RequestId, out var request) || request.TenantId != signal.TenantId || request.Revision != signal.DecisionRevision
                || request.Status == TenantApprovalStatus.Pending || request.RunId != signal.RunId || at < signal.CreatedAt) return ValueTask.FromResult(false);
            var p = request.Operation.AutomationAdmission ?? throw new ArgumentException("Exact Automation consent source required.");
            var source = automationBacking.ReadTenantOccurrence(request.TenantId, p.Occurrence.OccurrenceId);
            if (source is null || !TenantAutomationAdmissionApprovalRules.SameOccurrence(source, TenantAutomationAdmissionApprovalRules.Pending(p))) return ValueTask.FromResult(false);
            var grant = _operationGrants.GetValueOrDefault(request.RequestId); var complete = false;
            var reason = request.Status == TenantApprovalStatus.Approved ? refusal : TenantApprovalRules.DuplicateReason(request);
            if (request.Status == TenantApprovalStatus.Approved)
            {
                try
                {
                    var agent = RequireAutomationAdmissionOperation(request, state, ct);
                    var decision = _approvalDecisions.GetValueOrDefault(request.RequestId) ?? throw new AccessDeniedException("approval_unavailable");
                    TenantAutomationAdmissionApprovalRules.Decision(request, decision, grant, at);
                    TenantAutomationAdmissionApprovalRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
                    if (admission is not null)
                    {
                        if (admission.Run.CreatedAtUtc < signal.CreatedAt || admission.Run.CreatedAtUtc > at) throw new SecurityConflictException("operation_witness_changed");
                        TenantAutomationRules.Admission(admission, agent, request.OriginalInitiatorPrincipalId, p); RequireTenantEventAdmission(admission, p, true, ct); complete = true;
                    }
                    else if (string.IsNullOrWhiteSpace(reason)) throw new SecurityConflictException("operation_mismatch");
                }
                catch (AccessDeniedException error) { reason = error.ReasonCode; }
                catch (SecurityConflictException error) { reason = error.ReasonCode; }
            }
            ct.ThrowIfCancellationRequested();
            if (complete) StageTenantBackgroundGraph(admission!.SessionOwner, admission.Session, admission.Run);
            automationBacking.ReplaceTenantOccurrence(complete ? admission!.Occurrence : source.WithRouting(OccurrenceRoutingDisposition.Rejected,
                reason ?? "automation_admission_unavailable", source.RoutingRevision + 1, at, null, null));
            SettleTenantEventSource(p.EventSource, p.Occurrence.OccurrenceId, false, complete ? null : reason ?? "automation_admission_unavailable");
            if (grant is { Status: OperationGrantStatus.Available }) _operationGrants[request.RequestId] = grant with { Status = complete ? OperationGrantStatus.Consumed : OperationGrantStatus.Revoked, Revision = grant.Revision + 1 };
            _approvalSignals[signal.SignalId] = signal with { Revision = signal.Revision + 1, ProcessedAt = at };
            _tenants[request.TenantId] = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
            _audit.Add(new(auditId, request.TenantId, request.ExecutorPrincipalId, "automations.admission.settle", p.Current.AutomationId.ToString("D"), complete ? "allowed" : "denied", complete ? "automation_occurrence_admitted" : reason ?? "automation_admission_unavailable", at));
            return ValueTask.FromResult(true);
        }
    }
}
