using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public sealed record TenantHumanTaskConsentTarget(TenantSession Source, TenantAgent SourceAgent, TenantAgent Worker, ResolvedAgentRunConfiguration Configuration);

public sealed record TenantTaskConsentTarget(TenantSession Source, TenantAgent SourceAgent, AgentRun OriginalSource,
    TenantAgent Worker, ResolvedAgentRunConfiguration Configuration);

/// <summary>Re-resolves the exact two accountable Agents. Source consent never supplies worker permissions.</summary>
public sealed class TenantTaskDelegationTarget(ITenantSessionStore sessions, ITenantRunStore runs, TenantRunConfigurationResolver configuration,
    ITenantModelCatalogReader models, IamAuthorizer iam, TimeProvider time)
{
    public async ValueTask<TenantTaskConsentTarget> RequireAsync(TenantApprovalRequest request, AgentRun run, TenantSecurityState state, CancellationToken ct)
    {
        var prepared = request.Operation.TaskDelegation ?? throw new AccessDeniedException("task_review_unavailable"); prepared.Validate();
        var task = prepared.Task;
        if (task.Source.ActorKind != TaskActorKind.Agent || task.Source.RunId is not { } sourceRunId)
            throw new AccessDeniedException("task_review_detached_owner_required");
        var original = await runs.FindTenantRunAsync(request.TenantId, sourceRunId, ct) ?? throw new AccessDeniedException("task_source_unavailable");
        var (sourceAgent, current) = await configuration.CurrentPolicyAsync(request.TenantId, task.Source.InstanceId, ct);
        var source = await sessions.FindTenantSessionAsync(request.TenantId, task.Source.SessionId, ct) ?? throw new AccessDeniedException("task_source_unavailable");
        await TenantTaskConsentRules.RequireModelSourceAsync(request, run, original, source, sourceAgent, current, state, iam, time.GetUtcNow(), ct);
        var resolved = await RequireWorkerAsync(request, state, ct);
        return new(source, sourceAgent, original, resolved.Agent, resolved.Configuration);
    }
    private async ValueTask<(TenantAgent Agent, ResolvedAgentRunConfiguration Configuration)> RequireWorkerAsync(TenantApprovalRequest request, TenantSecurityState state, CancellationToken ct)
    {
        var task = request.Operation.TaskDelegation!.Task;
        var resolved = await configuration.ResolveInSpaceAsync(request.TenantId, task.Assignment.InstanceId,
            new(request.TenantId, task.Assignment.SpaceId, task.Assignment.EnvironmentId, task.Assignment.EnvironmentSelectionEpoch, 1, task.CreatedAt), ct);
        await TaskAuthority.TargetAsync(state, task.Source.PrincipalId, resolved.Agent, iam, ct);
        if (resolved.Agent.PrincipalId != task.Assignment.PrincipalId || resolved.Agent.Instance.Revision != task.Assignment.InstanceRevision
            || TaskLimits.Hash(resolved.Configuration.Configuration) != task.Assignment.ConfigurationHash || task.DeadlineAt <= time.GetUtcNow())
            throw new AccessDeniedException("task_assignment_changed");
        var catalog = await models.ReadAsync(state, ct); var pin = task.Assignment.Model!;
        if (!ExecutionModelPolicy.Matches(catalog, new(pin.OfferingId, pin.ConnectionId, pin.ExternalModelId, pin.ReasoningEffort,
            ExecutionModelSource.ConversationDefault, pin.Binding, pin.Decision), out _)) throw new AccessDeniedException("task_model_unavailable");
        return (resolved.Agent, resolved.Configuration);
    }
    public async ValueTask<TenantHumanTaskConsentTarget> RequireHumanAsync(TenantApprovalRequest request, TenantSecurityState state, CancellationToken ct)
    {
        TenantTaskConsentRules.RequireHumanShape(request); var task = request.Operation.TaskDelegation!.Task;
        var source = await sessions.FindTenantSessionAsync(request.TenantId, task.Source.SessionId, ct) ?? throw new AccessDeniedException("task_source_unavailable");
        var (agent, _) = await configuration.CurrentPolicyAsync(request.TenantId, task.Source.InstanceId, ct);
        await TenantTaskConsentRules.RequireHumanSourceAsync(request, source, agent, state, iam, time.GetUtcNow(), ct);
        var worker = await RequireWorkerAsync(request, state, ct);
        return new(source, agent, worker.Agent, worker.Configuration);
    }
    public static TenantHumanTaskAdmission BuildHuman(TenantApprovalRequest request, TenantHumanTaskConsentTarget current, long securityRevision, Guid auditId)
    {
        TenantTaskConsentRules.RequireHumanShape(request); var p = request.Operation.TaskDelegation!; var task = p.Task;
        var graph = task.State == DelegatedTaskState.PendingDependencies ? ((SessionSnapshot?)null, (AgentRun?)null)
            : TaskAdmissionFactory.Worker(task, current.Worker, current.Configuration, task.Assignment.Model!, task.WorkerSessionId!.Value,
                p.WorkerEntryId!.Value, p.WorkerActivationId!.Value, task.InitialWorkerRunId!.Value, p.WorkerResponseId!.Value, task.CreatedAt);
        var admission = new TenantHumanTaskAdmission(task, graph.Item1, graph.Item2, securityRevision, auditId);
        TaskAdmissionFactory.RequireHumanGraph(admission, current.Source, current.Worker, task.CreatedAt);
        return admission;
    }
    public static TaskAdmission Build(TenantApprovalRequest request, TenantTaskConsentTarget current, long securityRevision, Guid auditId)
    {
        var p = request.Operation.TaskDelegation ?? throw new AccessDeniedException("task_review_unavailable"); p.Validate();
        var actor = new TaskActor(Agent: new(current.OriginalSource.Owner, current.OriginalSource.SessionId,
            current.OriginalSource.AgentRunId, p.SourceGeneration!.Value), Tool: "tasks.delegate");
        var task = p.Task;
        var graph = task.State == DelegatedTaskState.PendingDependencies ? ((SessionSnapshot?)null, (AgentRun?)null)
            : TaskAdmissionFactory.Worker(task, current.Worker, current.Configuration, task.Assignment.Model!, task.WorkerSessionId!.Value,
                p.WorkerEntryId!.Value, p.WorkerActivationId!.Value, task.InitialWorkerRunId!.Value, p.WorkerResponseId!.Value, task.CreatedAt);
        var admission = new TaskAdmission(request.TenantId, task.Source.SessionId, actor, task.Request, task, graph.Item1, graph.Item2, securityRevision, auditId);
        TaskAdmissionFactory.RequireGraph(admission, current.Source, current.OriginalSource, current.Worker, task.CreatedAt);
        return admission;
    }
}
