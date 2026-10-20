using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantBackgroundCompletion?> ReadTenantBackgroundCompletionAsync(AgentRunOwner owner, Guid childRunId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantRuntimeRules.RequireOwner(owner);
        lock (_gate) lock (sessionBacking.AdmissionGate) return ValueTask.FromResult(CompletionSource(owner, childRunId));
    }
    private TenantBackgroundCompletion? CompletionSource(AgentRunOwner owner, Guid childRunId)
    {
        var run = sessionBacking.ReadRetainedTenantOperationRun(owner.TenantId!.Value, childRunId);
        if (run?.Owner != owner || sessionBacking.ReadRetainedTenantBackgroundSession(owner.TenantId.Value, run.SessionId) is not { } child) return null;
        var parent = child.Snapshot.Origin.OriginatingSessionId is { } parentId ? sessionBacking.ReadTenantSession(owner.TenantId.Value, parentId) : null;
        var initial = TenantBackgroundLineage.InitialAsync(run, (id, _) => ValueTask.FromResult(sessionBacking.ReadRetainedTenantOperationRun(owner.TenantId.Value, id)), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return new(run, child, parent, initial, sessionBacking.AgentRuns.CompletionReceipts.TryGetValue(childRunId, out var receipt) ? CompletionInboxMapping.Read(receipt) : null);
    }
    public ValueTask<IReadOnlyList<TenantBackgroundCompletion>> ListTenantBackgroundCompletionsAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var values = sessionBacking.AgentRuns.CompletionReceipts.Values.Where(r => r.ProfileId is null && r.Status == (int)CompletionInboxStatus.Pending)
                .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.ChildAgentRunId).Select(CompletionInboxMapping.Read)
                .Where(i => i.Owner.TenantId is not null).Select(i => CompletionSource(i.Owner, i.ChildAgentRunId)).OfType<TenantBackgroundCompletion>()
                .Where(source => source.Parent is null || source.Parent.Snapshot.PendingAgentInputIds.Count == 0
                    && !sessionBacking.AgentRuns.Runs.Values.Any(run => run.SessionId == source.Parent.Owner.SessionId && !run.IsTerminal))
                .Where(source => !_approvals.Values.Any(r => r.TenantId == source.Child.Owner.TenantId && r.ExecutorPrincipalId == source.Child.Owner.ExecutorPrincipalId
                    && r.RootActivationId == source.Child.Admission.ExecutionContext!.RootActivationId
                    && (r.Operation.BackgroundReport?.ChildRunId == source.Child.AgentRunId || r.Operation.ReportSourceRead?.SourceRunId == source.Child.AgentRunId
                        && (r.Status != TenantApprovalStatus.Approved || !_operationReceipts.TryGetValue(r.Operation.OperationId, out var receipt)
                            || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != TenantReportSourceReadRules.Reason))))
                .Take(limit).ToArray();
            return ValueTask.FromResult<IReadOnlyList<TenantBackgroundCompletion>>(values);
        }
    }
    public ValueTask SkipTenantBackgroundCompletionAsync(AgentRunOwner owner, Guid childRunId, string reason, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantRuntimeRules.RequireOwner(owner);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 80) throw new ArgumentException("Use a bounded reason code.");
        lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            if (CompletionSource(owner, childRunId) is null || !sessionBacking.AgentRuns.CompletionReceipts.TryGetValue(childRunId, out var row)) return ValueTask.CompletedTask;
            var item = CompletionInboxMapping.Read(row);
            if (item.Owner == owner && item.Status == CompletionInboxStatus.Pending)
                CompletionInboxMapping.Write(row, item with { Revision = item.Revision + 1, Status = CompletionInboxStatus.Skipped, SkipReason = reason });
            return ValueTask.CompletedTask;
        }
    }
    public ValueTask<AgentRunAdmissionResult> AdmitTenantBackgroundCompletionAsync(AgentRun report, long expectedParentRevision, Guid auditId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantRuntimeRules.RequireOwner(report.Owner); if (auditId == Guid.Empty) throw new ArgumentException("Audit identity is required.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            RequireRuntimeOwner(report.Owner); var tenant = report.Owner.TenantId!.Value; var state = _tenants[tenant];
            var source = CompletionSource(report.Owner, report.Admission.ExecutionContext?.ParentRunId ?? Guid.Empty) ?? throw new AccessDeniedException("completion_unavailable");
            RequireTenantReportRead(state, source, ct);
            var all = sessionBacking.AgentRuns;
            if (!all.CompletionReceipts.TryGetValue(source.Child.AgentRunId, out var row)) throw new AccessDeniedException("completion_unavailable");
            var item = CompletionInboxMapping.Read(row); if (item.Owner != report.Owner) throw new AccessDeniedException("completion_unavailable");
            if (item.Status != CompletionInboxStatus.Pending)
            {
                if (item.ReportActivationId is { } activation && all.Runs.Values.SingleOrDefault(r => r.Owner == report.Owner && r.ActivationId == activation) is { } existing)
                    return ValueTask.FromResult(new AgentRunAdmissionResult(false, existing));
                throw new AccessDeniedException("completion_unavailable");
            }
            if (_approvals.Values.Any(r => r.TenantId == tenant && r.Operation.BackgroundReport?.ChildRunId == source.Child.AgentRunId))
                throw new AccessDeniedException("report_approval_required");
            var agent = FindTenantAgentAsync(tenant, report.AgentInstanceId, ct).Result!;
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            TenantBackgroundCompletionRules.Shape(source, report, agent, expectedParentRevision, now);
            var root = TenantExecutionLineage.RootAsync(source.Child, this, ct).AsTask().GetAwaiter().GetResult();
            var baseline = Guid.TryParse(agent.Instance.DefinitionId, out var definition)
                ? definitions.TenantPublication(tenant, definition, agent.Instance.ActiveVersion)?.Payload
                : builtIns?.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion, ct).AsTask().GetAwaiter().GetResult();
            if (baseline is null) throw new AccessDeniedException("definition_unavailable");
            TenantBackgroundAdmissionRules.AuthorizeReportAsync(state, root, InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides), new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
            if (source.Parent!.Snapshot.PendingAgentInputIds.Count > 0 || all.Runs.Values.Any(r => r.SessionId == report.SessionId && !r.IsTerminal))
                throw new SecurityConflictException("parent_busy");
            if (all.Runs.ContainsKey(report.AgentRunId) || all.Activations.ContainsKey(report.ActivationId) || _audit.Any(a => a.AuditId == auditId)) throw new SecurityConflictException("identity_conflict");
            var activationRow = AgentRunStoreMapping.ToActivationRecord(source.Parent.Snapshot, report);
            var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
            var audit = new AuthorizationAudit(auditId, tenant, report.Owner.ExecutorPrincipalId!.Value, "agent-runs.background.report", report.AgentRunId.ToString("D"), "allowed", "owned_completion_admitted", now);
            ct.ThrowIfCancellationRequested(); all.Runs.Add(report.AgentRunId, report); all.Activations.Add(report.ActivationId, activationRow);
            all.ByAdmission.Add((report.SessionId, report.Admission.Activation.DedupeKey), report.AgentRunId);
            CompletionInboxMapping.Write(row, item with { Revision = item.Revision + 1, Status = CompletionInboxStatus.DeliveryQueued, ReportActivationId = report.ActivationId });
            _tenants[tenant] = next; _audit.Add(audit); return ValueTask.FromResult(new AgentRunAdmissionResult(true, report));
        }
    }
}
