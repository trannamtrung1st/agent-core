using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantBackgroundCreationTarget(ITenantSessionStore sessions, ITenantRunStore runs, TenantRunConfigurationResolver configuration, IamAuthorizer iam)
{
    public async ValueTask<(TenantAgent Agent, ResolvedAgentRunConfiguration Configuration, TenantSession Session)> SnapshotAsync(AgentRun run, CancellationToken ct)
    {
        var current = await configuration.ResolveInCapturedSpaceAsync(run, ct, selectionRevision: 1).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(run.Owner.TenantId!.Value, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        return (current.Agent, current.Configuration, session);
    }
    public async ValueTask<(TenantAgent Agent, ResolvedAgentRunConfiguration Configuration, TenantSession Session)> RequireAsync(TenantApprovalRequest request, AgentRun run, TenantSecurityState state, CancellationToken ct)
    {
        var current = await configuration.ResolveInCapturedSpaceAsync(run, ct, selectionRevision: 1).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(request.TenantId, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantWorkspaceEffectRules.Session(run, current.Agent, session);
        TenantBackgroundCreationRules.Require(request, run, current.Agent, current.Configuration.Configuration.Definition);
        if (current.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || current.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision
            || TenantBackgroundCreationRules.Hash(current.Configuration) != request.Operation.BackgroundStart!.ConfigurationHash)
            throw new AccessDeniedException("operation_witness_changed");
        await TenantExecutionLineage.RequireRequesterAsync(run, state, iam, runs, ct).ConfigureAwait(false);
        await TenantBackgroundCreationRules.ExecutorAsync(state, request, iam, ct).ConfigureAwait(false);
        return (current.Agent, current.Configuration, session);
    }
    public static TenantBackgroundAdmission Build(TenantApprovalRequest request, AgentRun run, TenantSession session, ResolvedAgentRunConfiguration resolved, DateTimeOffset at)
    {
        var p = request.Operation.BackgroundStart!;
        var child = BackgroundSessionAdmissionFactory.ForImmediate(p.ChildSessionId, p.EntryId, p.ActivationId, p.RunId, p.ResponseId, session.Snapshot, run,
            "operation:" + request.Operation.OperationId.ToString("D"), p.Objective, p.Title, p.ReportCompletion, at,
            ExecutionBudgetResolver.Resolve(resolved.Configuration.Definition, resolved.InstanceBudgets, false), resolved, p.Model.ToPin());
        return new(child.Session, child.Run);
    }
}
