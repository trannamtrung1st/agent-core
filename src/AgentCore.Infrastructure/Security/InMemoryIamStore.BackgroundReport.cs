using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private TenantBackgroundCompletion RequireBackgroundReportOperation(TenantApprovalRequest request, AgentRun child, TenantAgent agent)
    {
        var source = CompletionSource(child.Owner, child.AgentRunId) ?? throw new AccessDeniedException("completion_unavailable");
        var state = _tenants[request.TenantId]; var iam = new IamAuthorizer(this);
        var current = InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
        TenantApprovalRules.Validate(request, child, agent); TenantSessionRules.RequireExecutor(state, agent);
        TenantBackgroundReportRules.Require(request, source, agent, current);
        TenantExecutionLineage.RequireRequesterAsync(child, state, iam, this, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var root = TenantExecutionLineage.RootAsync(child, this, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        TenantBackgroundReportRules.ExecutorAsync(state, request, root, current, iam, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return source;
    }
    public ValueTask<bool> SettleTenantBackgroundReportApprovalAsync(ApprovalProcessedSignal signal, AgentRun? report, string? refusal, Guid auditId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (auditId == Guid.Empty || refusal is { Length: > 80 } || report is not null && refusal is not null) throw new ArgumentException("Invalid report settlement.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            if (!_approvalSignals.TryGetValue(signal.SignalId, out var saved) || saved != signal || saved.ProcessedAt is not null
                || !_approvals.TryGetValue(signal.RequestId, out var request) || request.Operation.BackgroundReport is null
                || request.Status == TenantApprovalStatus.Pending || request.Revision != signal.DecisionRevision || request.TenantId != signal.TenantId || request.RunId != signal.RunId)
                return ValueTask.FromResult(false);
            var p = request.Operation.BackgroundReport; var now = ApprovalNow;
            if (now < signal.CreatedAt || !_tenants.TryGetValue(request.TenantId, out var state)
                || !sessionBacking.AgentRuns.CompletionReceipts.TryGetValue(request.RunId, out var row)) return ValueTask.FromResult(false);
            var item = CompletionInboxMapping.Read(row);
            if (item.Owner != AgentRunOwner.ForTenant(request.TenantId, request.Operation.InstanceId, request.ExecutorPrincipalId)) throw new SecurityConflictException("operation_mismatch");
            var source = CompletionSource(item.Owner, request.RunId); var agent = FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId, ct).Result;
            var approved = request.Status == TenantApprovalStatus.Approved;
            var reason = approved ? refusal : TenantApprovalRules.DuplicateReason(request);
            _operationGrants.TryGetValue(request.RequestId, out var grant);
            if (approved && source?.Parent is { } pendingParent && (pendingParent.Snapshot.PendingAgentInputIds.Count > 0
                || sessionBacking.AgentRuns.Runs.Values.Any(r => r.SessionId == p.ParentSessionId && !r.IsTerminal))) return ValueTask.FromResult(false);
            if (approved)
            {
                try
                {
                    var decision = _approvalDecisions.GetValueOrDefault(request.RequestId) ?? throw new AccessDeniedException("approval_unavailable");
                    TenantBackgroundReportRules.Decision(request, decision, grant, now);
                    if (source is null || agent is null) throw new AccessDeniedException("completion_unavailable");
                    source = RequireBackgroundReportOperation(request, source.Child, agent);
                    TenantBackgroundReportRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
                    if (report is not null)
                    {
                        TenantBackgroundReportRules.Report(request, source, report, agent, now);
                        if (source.Parent!.Snapshot.PendingAgentInputIds.Count > 0 || sessionBacking.AgentRuns.Runs.Values.Any(r => r.SessionId == p.ParentSessionId && !r.IsTerminal))
                            return ValueTask.FromResult(false);
                    }
                    else if (string.IsNullOrWhiteSpace(reason)) throw new SecurityConflictException("operation_mismatch");
                }
                catch (AccessDeniedException error) { reason = error.ReasonCode; }
                catch (SecurityConflictException error) when (error.ReasonCode != "operation_mismatch") { reason = error.ReasonCode; }
            }
            else if (report is not null) throw new SecurityConflictException("operation_mismatch");
            var complete = approved && reason is null && report is not null;
            var all = sessionBacking.AgentRuns;
            if (_audit.Any(a => a.AuditId == auditId) || approved && _operationReceipts.ContainsKey(request.Operation.OperationId)) throw new SecurityConflictException("identity_conflict");
            if (complete && (all.Runs.ContainsKey(report!.AgentRunId) || all.Activations.ContainsKey(report.ActivationId)
                || all.ByAdmission.ContainsKey((report.SessionId, report.Admission.Activation.DedupeKey)))) throw new SecurityConflictException("identity_conflict");
            var activation = complete ? AgentRunStoreMapping.ToActivationRecord(source!.Parent!.Snapshot, report!) : null;
            var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
            ct.ThrowIfCancellationRequested();
            if (complete)
            {
                all.Runs.Add(report!.AgentRunId, report); all.Activations.Add(report.ActivationId, activation!);
                all.ByAdmission.Add((report.SessionId, report.Admission.Activation.DedupeKey), report.AgentRunId);
            }
            if (item.Status == CompletionInboxStatus.Pending)
                CompletionInboxMapping.Write(row, item with { Revision = item.Revision + 1, Status = complete ? CompletionInboxStatus.DeliveryQueued : CompletionInboxStatus.Skipped,
                    ReportActivationId = complete ? report!.ActivationId : null, SkipReason = complete ? null : reason });
            if (grant is not null) _operationGrants[request.RequestId] = grant with { Status = complete ? OperationGrantStatus.Consumed : OperationGrantStatus.Revoked, Revision = grant.Revision + 1 };
            var code = complete ? "completion_report_created" : reason ?? "completion_unavailable";
            if (approved) _operationReceipts.Add(request.Operation.OperationId, new(request.Operation.OperationId, request.RequestId, request.TenantId, request.RunId, request.SessionId,
                request.RootActivationId, request.ExecutorPrincipalId, request.Operation.InstanceId, request.Operation.ActionHash, complete ? TenantOperationOutcome.Completed : TenantOperationOutcome.Denied,
                code, agent?.Instance.Revision ?? request.Operation.ExpectedInstanceRevision, agent?.Instance.PersonaRevision ?? request.Operation.ExpectedPersonaRevision, now));
            _approvalSignals[signal.SignalId] = signal with { Revision = signal.Revision + 1, ProcessedAt = now }; _tenants[request.TenantId] = next;
            _audit.Add(new(auditId, request.TenantId, request.ExecutorPrincipalId, "agent-runs.background.report", request.ResourceId, complete ? "allowed" : "denied", code, now));
            return ValueTask.FromResult(true);
        }
    }
}
