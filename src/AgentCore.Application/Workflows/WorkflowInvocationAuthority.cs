using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public static class WorkflowInvocationAuthority
{
    public static async ValueTask RequireClaimedRecordAsync(TenantSecurityState state, WorkflowExecution execution, AgentRun run,
        AgentRun root, TenantAgent agent, AgentDefinition current, ITenantRunStore runs, IamAuthorizer iam, CancellationToken ct)
    {
        var source = execution.Source;
        if (source.Kind != WorkflowInvocationKind.ClaimedAgent || source.RunId != run.AgentRunId || source.SourceId != run.AgentRunId
            || run.Owner.TenantId != execution.TenantId || source.SessionId != run.SessionId || source.PrincipalId != run.Owner.ExecutorPrincipalId
            || source.InvokingInstanceId != run.AgentInstanceId || source.RootActivationId != run.Admission.Activation.RootActivationId
            || source.OriginalHumanPrincipalId != run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId || source.AccountId is not null
            || source.Causation != WorkflowCausationRules.FromRoot(root) || run.Admission.OutputContract == AgentRunOutputContract.CompletionReport || run.CancellationRequested || run.RequiresEffectReconciliation
            || agent.Instance.InstanceId != run.AgentInstanceId || agent.PrincipalId != source.PrincipalId
            || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active || !RolePermissions.AllowsTool(current, "workflows.start")
            || run.Admission.Configuration is null || !RolePermissions.AllowsTool(run.Admission.Configuration.Definition, "workflows.start"))
            throw new AccessDeniedException("workflow_source_unavailable");
        TenantSessionRules.RequireExecutor(state, agent);
        await TenantExecutionLineage.RequireRequesterAsync(run, state, iam, runs, ct);
        await TaskAuthority.RequireAsync(state, source.PrincipalId, "sessions.read", TenantSessionRules.Scope(execution.TenantId, run.SessionId), iam, ct);
        if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, source.OriginalHumanPrincipalId, "workflows.start",
            WorkflowCatalogAuthority.Scope(execution.TenantId, execution.WorkflowId), iam, ct)) throw new AccessDeniedException("workflow_review_required");
        if (root.Admission.DelegatedTask is { } task && runs is ITenantTaskStore tasks
            && (await tasks.FindTaskAsync(execution.TenantId, task.TaskId, ct))?.Source.Workflow is not null)
            throw new AccessDeniedException("workflow_recursion_forbidden");
    }
}
