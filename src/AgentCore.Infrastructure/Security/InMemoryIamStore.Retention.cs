using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantSessionRetentionTarget?> FindExpiredSessionAsync(Guid tenant, Guid session, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            var retained = sessionBacking.ReadRetainedTenantBackgroundSession(tenant, session);
            return ValueTask.FromResult(retained is not null && SessionRetention.IsDue(retained.Snapshot, (clock ?? TimeProvider.System).GetUtcNow())
                ? new TenantSessionRetentionTarget(retained.Owner, retained.Snapshot.Revision, retained.Snapshot.RetentionDueAtUtc!.Value) : null);
        }
    }
    public ValueTask<IReadOnlyList<TenantSessionRetentionTarget>> ListDueSessionsAsync(TenantSessionRetentionTarget? after, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
            return ValueTask.FromResult(sessionBacking.DueTenantSessions(after, Math.Clamp(limit, 1, 100)));
    }
    public ValueTask<bool> RetireDueSessionAsync(TenantSessionRetentionTarget target, Guid auditId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (auditId == Guid.Empty) throw new ArgumentException("Audit identity required.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            var current = sessionBacking.DueTenantSession(target);
            if (current is null || !_tenants.TryGetValue(target.Owner.TenantId, out var state)) return ValueTask.FromResult(false);
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            var all = sessionBacking.AgentRuns.Runs.Values.Where(r => r.Owner.TenantId == target.Owner.TenantId && r.SessionId == target.Owner.SessionId).ToArray();
            var approvalIds = _approvals.Values.Where(a => a.TenantId == target.Owner.TenantId && a.SessionId == target.Owner.SessionId).Select(a => a.RequestId).ToHashSet();
            foreach (var grant in _operationGrants.Values.Where(g => g.TenantId == target.Owner.TenantId && approvalIds.Contains(g.RequestId) && g.Status == OperationGrantStatus.Available).ToArray())
                _operationGrants[grant.RequestId] = grant with { Status = OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) };
            foreach (var approval in _approvals.Values.Where(a => a.TenantId == target.Owner.TenantId && a.SessionId == target.Owner.SessionId && a.Status == TenantApprovalStatus.Pending).ToArray())
                _approvals[approval.RequestId] = approval.Decide(now >= approval.ExpiresAt ? TenantApprovalStatus.Expired : TenantApprovalStatus.Cancelled, approval.Revision, now);
            if (all.Any(r => r.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate))
            {
                foreach (var run in all.Where(r => !r.IsTerminal))
                {
                    var requested = TenantRunCancellationRules.Cancel(run, now);
                    sessionBacking.SaveRetainedTenantOperationRun(requested, run.Revision); AppendTenantCoreRun(run, requested); SettleTenantCompletion(requested, run.Claim?.Generation);
                }
                sessionBacking.ExpireDueTenantSession(target, now);
                if (!SessionLifecycle.IsTerminal(current.LifecycleStatus))
                {
                    AppendTenantCoreSession(new(target.Owner, current), sessionBacking.ReadRetainedTenantBackgroundSession(target.Owner.TenantId, target.Owner.SessionId)!);
                    _audit.Add(new(auditId, target.Owner.TenantId, Guid.Empty, "sessions.retention.expire", target.Owner.SessionId.ToString("D"), "allowed", "system_due_effect_reconciliation_pending", now));
                }
                return ValueTask.FromResult(false);
            }
            foreach (var run in all.Where(r => !r.IsTerminal))
            {
                var next = TenantRunCancellationRules.Cancel(run, now);
                sessionBacking.SaveRetainedTenantOperationRun(next, run.Revision); AppendTenantCoreRun(run, next); SettleTenantCompletion(next, run.Claim?.Generation);
            }
            var attachmentIds = state.Assignments.Where(a => TenantAttachmentRules.TryId(a.Scope, out var id) && attachmentBacking?.RetainedTenant(target.Owner.TenantId, target.Owner.SessionId, id) is not null)
                .Select(a => Guid.Parse(a.Scope.ResourceId!)).ToHashSet();
            var nextState = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) }, Assignments = TenantSessionMutations.RetireAssignments(state.Assignments,
                target.Owner, all.Select(r => r.AgentRunId).ToHashSet(), attachmentIds, artifactBacking?.RetainedTenantArtifactIds(target.Owner.TenantId, target.Owner.SessionId) ?? new HashSet<Guid>()) };
            sessionBacking.DeleteDueTenantSession(target, now); TombstoneSessionMemory(target.Owner.TenantId, target.Owner.SessionId, now);
            var retired = sessionBacking.ReadRetainedTenantBackgroundSession(target.Owner.TenantId, target.Owner.SessionId)!;
            AppendTenantCoreSession(new(target.Owner, current), retired);
            _tenants[target.Owner.TenantId] = nextState;
            _audit.Add(new(auditId, target.Owner.TenantId, Guid.Empty, "sessions.retention.delete", target.Owner.SessionId.ToString("D"), "allowed", "system_due_session_retired", now));
            return ValueTask.FromResult(true);
        }
    }
}
