using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Trusted human/session references, never a copied permission snapshot or a model-supplied execution frame.
public sealed class TenantRunCancellationService(IIamStore security, ITenantRunStore runs, IdentityService identity,
    IamAuthorizer authorizer, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantRunCancellation> PrepareAsync(Guid tenant, Guid runId, TenantSessionInitiator initiator,
        long expectedSecurityRevision, long expectedRunRevision, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false);
        var run = await runs.FindTenantRunAsync(tenant, runId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        foreach (var (permission, scope) in new[] {
            ("agent-runs.cancel", TenantRunRules.Scope(tenant, runId)), ("agent-runs.read", TenantRunRules.Scope(tenant, runId)),
            ("sessions.read", TenantSessionRules.Scope(tenant, run.SessionId)),
            ("agents.chat", AccessScope.ForResource(tenant, "agent", run.AgentInstanceId.ToString("D"))) })
        {
            var decision = await authorizer.CheckTenantAsync(state, initiator.PrincipalId, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        if (state.Tenant.Revision != expectedSecurityRevision || run.Revision != expectedRunRevision) throw new SecurityConflictException("stale_revision");
        if (run.IsTerminal) throw new SecurityConflictException("run_in_use");
        return new(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation, initiator, expectedSecurityRevision, expectedRunRevision);
    }
    public async ValueTask<AgentRun> CommitAsync(TenantRunCancellation request, CancellationToken ct = default, ConversationEntry? response = null)
    {
        var tenant = request.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        if (await PrepareAsync(tenant, request.RunId, request.Initiator, request.ExpectedSecurityRevision, request.ExpectedRunRevision, ct).ConfigureAwait(false) != request)
            throw new AccessDeniedException("tenant_context_mismatch");
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        if (state.Tenant.Revision != request.ExpectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        var at = time.GetUtcNow();
        var audit = new AuthorizationAudit(ids.NewId(), tenant, request.Initiator.PrincipalId, "agent-runs.cancel", request.RunId.ToString("D"), "allowed", "run_cancelled", at);
        return await runs.TryCancelTenantRunAsync(state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } },
            state.Tenant.Revision, request.RunId, request.ExpectedRunRevision, request.Initiator, at, audit, ct, response).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
    }
}

public static class TenantRunCancellationRules
{
    public static ConversationEntry[] ResponseEntries(IReadOnlyList<ConversationEntry> before, AgentRun run, ConversationEntry? response, DateTimeOffset at)
    {
        var entries = before.ToList();
        if (response is not null)
        {
            if (run.Status != AgentRunStatus.Running || run.Claim is null || response.Role != ConversationRole.Assistant || response.ResponseId != run.ResponseId
                || response.Sequence < 1 || response.EntryId == Guid.Empty || response.ReceivedTextEndExclusive < 0 || response.HeardTextEndExclusive < 0
                || response.ReceivedTextEndExclusive > response.Text.Length
                || entries.Any(e => e.EntryId != response.EntryId && (e.Sequence == response.Sequence || e.Role == ConversationRole.Assistant && e.ResponseId == response.ResponseId)))
                throw new SecurityConflictException("cancellation_response_mismatch");
            var index = entries.FindIndex(e => e.EntryId == response.EntryId);
            if (index < 0) entries.Add(response);
            else
            {
                var prior = entries[index];
                if (prior.Role != response.Role || prior.ResponseId != response.ResponseId || prior.Sequence != response.Sequence
                    || !response.Text.StartsWith(prior.Text, StringComparison.Ordinal) || response.ReceivedTextEndExclusive < prior.ReceivedTextEndExclusive
                    || response.HeardTextEndExclusive < prior.HeardTextEndExclusive)
                    throw new SecurityConflictException("cancellation_response_mismatch");
                if (prior.Status == EntryStatus.Streaming) entries[index] = response;
            }
        }
        return entries.Select(e => Interrupt(e, run, at)).ToArray();
    }
    public static AgentRun Cancel(AgentRun run, DateTimeOffset at)
    {
        if (run.IsTerminal) throw new SecurityConflictException("run_in_use");
        var requested = run.RequestCancellation(run.Revision, null, at);
        return requested.Status == AgentRunStatus.Running
            ? requested.CommitCancellation(requested.Revision, requested.Claim!.Generation, null, at) : requested;
    }
    public static ConversationEntry Interrupt(ConversationEntry entry, AgentRun run, DateTimeOffset at) =>
        entry.Role == ConversationRole.Assistant && entry.ResponseId == run.ResponseId && entry.Status == EntryStatus.Streaming
            ? entry with { Status = EntryStatus.Interrupted, InterruptReason = "userStop", CompletedAt = at } : entry;
    public static TenantApprovalRequest? Approval(TenantApprovalRequest? request, AgentRun run, AgentRun? source, DateTimeOffset at)
    {
        if (request is null) return null;
        TenantApprovalContinuationRules.Require(request, run, source);
        if (request.RequestId != run.ApprovalRequestId || request.TenantId != run.Owner.TenantId || request.SessionId != run.SessionId
            || request.RootActivationId != run.Admission.ExecutionContext?.RootActivationId || request.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId)
            throw new SecurityConflictException("approval_continuation_mismatch");
        return request.Status == TenantApprovalStatus.Pending
            ? request.Decide(at >= request.ExpiresAt ? TenantApprovalStatus.Expired : TenantApprovalStatus.Cancelled, request.Revision, at) : request;
    }
}
