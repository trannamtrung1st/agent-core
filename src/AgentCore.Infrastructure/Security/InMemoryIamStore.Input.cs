using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantRuntimeInputStore
{
    public ValueTask<TenantHumanInputCommit> SaveTenantRuntimeInputAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot,
        long expectedRevision, TenantHumanInput input, Guid auditId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (attachmentBacking?.TenantGate ?? _gate)
        {
            RequireHumanSession(input.Owner.TenantId, input.Human);
            var state = _tenants[input.Owner.TenantId];
            var saved = sessionBacking.ReadTenantSession(input.Owner.TenantId, input.Owner.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var agent = FindTenantAgentAsync(input.Owner.TenantId, input.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("resource_unavailable");
            TenantHumanInputRules.RequireAsync(input, saved, agent, state, new IamAuthorizer(this), live: true, ct).AsTask().GetAwaiter().GetResult();
            if (frame.SessionId != input.Owner.SessionId || frame.Owner != AgentRunOwner.ForTenant(input.Owner.TenantId, input.Owner.AgentInstanceId, input.Owner.ExecutorPrincipalId))
                throw new AccessDeniedException("tenant_context_mismatch");
            var admission = input.Admission with { ExpectedSessionRevision = snapshot.Revision };
            var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
            var audit = new AuthorizationAudit(auditId, input.Owner.TenantId, input.Human.PrincipalId, "agent-runs.admit", admission.Run.AgentRunId.ToString("D"), "allowed", "human_message_admitted", (clock ?? TimeProvider.System).GetUtcNow());
            var result = CommitTenantHumanRun(next, state.Tenant.Revision, admission, input.Human, audit, ct, frame, snapshot, expectedRevision).Result
                ?? throw new SecurityConflictException("stale_revision");
            var visible = result.Created ? snapshot with { Revision = result.SessionRevision, LastEntrySequence = admission.Input.Sequence,
                LastUserActivityAt = snapshot.LastUserActivityAt > admission.Input.CreatedAt ? snapshot.LastUserActivityAt : admission.Input.CreatedAt, UpdatedAt = admission.Input.CreatedAt > snapshot.UpdatedAt ? admission.Input.CreatedAt : snapshot.UpdatedAt }
                : sessionBacking.LoadTenantRuntime(frame, (clock ?? TimeProvider.System).GetUtcNow())!;
            var stopped = result.Interrupted;
            if (stopped is not null) visible = TenantInputInterruptionRules.Interrupt(visible, stopped, stopped.UpdatedAtUtc);
            return ValueTask.FromResult(new TenantHumanInputCommit(result, visible, result.Created ? next.Tenant.Revision : state.Tenant.Revision, stopped));
        }
    }
}
