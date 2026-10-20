using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantRuntimeInputStore
{
    public async ValueTask<TenantHumanInputCommit> SaveTenantRuntimeInputAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot,
        long expectedRevision, TenantHumanInput input, Guid auditId, CancellationToken ct = default)
    {
        if (frame.SessionId != input.Owner.SessionId || frame.Owner != AgentRunOwner.ForTenant(input.Owner.TenantId, input.Owner.AgentInstanceId, input.Owner.ExecutorPrincipalId))
            throw new AccessDeniedException("tenant_context_mismatch");
        var state = await ReadTenantAsync(input.Owner.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (state.Tenant.Revision != input.SecurityRevision) throw new SecurityConflictException("stale_revision");
        var admission = input.Admission with { ExpectedSessionRevision = snapshot.Revision };
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var audit = new AuthorizationAudit(auditId, input.Owner.TenantId, input.Human.PrincipalId, "agent-runs.admit", admission.Run.AgentRunId.ToString("D"), "allowed", "human_message_admitted", (clock ?? TimeProvider.System).GetUtcNow());
        var result = await CommitTenantHumanRunAsync(next, state.Tenant.Revision, admission, input.Human, audit, ct, input, frame, snapshot, expectedRevision).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
        var visible = result.Created ? snapshot with { Revision = result.SessionRevision, LastEntrySequence = admission.Input.Sequence,
            LastUserActivityAt = snapshot.LastUserActivityAt > admission.Input.CreatedAt ? snapshot.LastUserActivityAt : admission.Input.CreatedAt, UpdatedAt = admission.Input.CreatedAt > snapshot.UpdatedAt ? admission.Input.CreatedAt : snapshot.UpdatedAt }
            : await LoadTenantRuntimeAsync(frame, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_runtime_unavailable");
        var stopped = result.Interrupted;
        if (stopped is not null) visible = TenantInputInterruptionRules.Interrupt(visible, stopped, stopped.UpdatedAtUtc);
        return new(result, visible, result.Created ? next.Tenant.Revision : state.Tenant.Revision, stopped);
    }
}
