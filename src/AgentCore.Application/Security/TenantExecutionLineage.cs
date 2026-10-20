using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Stored lineage is attribution and causal fencing, never a permission snapshot.
public static class TenantExecutionLineage
{
    public static async ValueTask RequireRequesterAsync(AgentRun run, TenantSecurityState state, IamAuthorizer iam, ITenantRunStore runs, CancellationToken ct)
    {
        var root = await RootAsync(run, runs, ct).ConfigureAwait(false);
        if (run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is not { } requester) return;
        foreach (var (key, scope) in new[] { ("sessions.read", TenantSessionRules.Scope(state.Tenant.TenantId, root.SessionId)),
            ("agents.chat", AccessScope.ForResource(state.Tenant.TenantId, "agent", run.AgentInstanceId.ToString("D"))) })
            if (!(await iam.CheckOwnedTenantAsync(state, requester, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("requester_unavailable");
    }
    public static async ValueTask<AgentRun> RootAsync(AgentRun run, ITenantRunStore runs, CancellationToken ct)
    {
        var initial = run.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_context_required");
        var visited = new HashSet<Guid>();
        for (var depth = 0; depth < 64; depth++)
        {
            var context = run.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_lineage_unavailable");
            if (!visited.Add(run.AgentRunId) || run.Owner.TenantId != initial.TenantId || run.Owner.ExecutorPrincipalId != initial.AgentPrincipalId
                || context.OriginalInitiatorPrincipalId != initial.OriginalInitiatorPrincipalId || context.RootActivationId != initial.RootActivationId)
                throw new AccessDeniedException("tenant_lineage_unavailable");
            if (context.Origin == ExecutionOrigin.UserMessage)
            {
                if (run.Admission.Activation.Kind != ActivationKind.UserTurn || context.RootActivationId != run.ActivationId
                    || context.ParentRunId is not null || context.ParentActivationId is not null)
                    throw new AccessDeniedException("tenant_lineage_unavailable");
                return run;
            }
            if (context.Origin is not (ExecutionOrigin.ImmediateBackground or ExecutionOrigin.CompletionReport or ExecutionOrigin.ApprovalContinuation)
                || context.ParentRunId is not { } parentId || context.ParentActivationId is not { } activationId)
                throw new AccessDeniedException("tenant_lineage_unavailable");
            var parent = await runs.FindTenantRunAsync(initial.TenantId, parentId, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("tenant_lineage_unavailable");
            var a = run.Admission.Activation;
            if (parent.Owner != run.Owner || parent.ActivationId != activationId || a.ParentActivationId != activationId
                || a.SourceAgentRunId != parentId || a.SourceSessionId != parent.SessionId)
                throw new AccessDeniedException("tenant_lineage_unavailable");
            if (context.Origin == ExecutionOrigin.ImmediateBackground
                && (a.Kind != ActivationKind.ImmediateBackground || parent.Admission.ExecutionContext?.Origin != ExecutionOrigin.UserMessage
                    || run.SessionId == parent.SessionId || run.Admission.OutputContract != AgentRunOutputContract.BackgroundOutcome))
                throw new AccessDeniedException("tenant_lineage_unavailable");
            if (context.Origin == ExecutionOrigin.CompletionReport
                && (a.Kind != ActivationKind.BackgroundCompleted || !parent.IsTerminal || parent.Admission.OutputContract != AgentRunOutputContract.BackgroundOutcome
                    || (await TenantBackgroundLineage.InitialAsync(parent, (id, token) => runs.FindTenantRunAsync(initial.TenantId, id, token), ct).ConfigureAwait(false)).Admission.Activation.SourceSessionId != run.SessionId
                    || run.Admission.OutputContract != AgentRunOutputContract.CompletionReport))
                throw new AccessDeniedException("tenant_lineage_unavailable");
            if (context.Origin == ExecutionOrigin.ApprovalContinuation
                && (a.Kind != ActivationKind.ApprovalContinuation || parent.SessionId != run.SessionId || parent.Admission.OutputContract != run.Admission.OutputContract
                    || parent.Result?.ContinuationRunId != run.AgentRunId || parent.Result.OutcomeKind != AgentRunOutcomeKind.Continued))
                throw new AccessDeniedException("tenant_lineage_unavailable");
            run = parent;
        }
        throw new AccessDeniedException("tenant_lineage_unavailable");
    }
}
