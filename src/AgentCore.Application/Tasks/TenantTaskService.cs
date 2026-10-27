using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Tasks;

public sealed class TenantTaskService(IIamStore security, ITenantTaskStore tasks, ITenantSessionStore sessions,
    ITenantRunStore runs, ITenantSpaceStore spaces, TenantRunConfigurationResolver configurations,
    ITenantModelCatalogReader models, IamAuthorizer iam, IIdGenerator ids, TimeProvider time, AutoModelRouter? routing = null)
{
    public async ValueTask<TaskMutationResult> DelegateAsync(Guid tenant, Guid sourceSession, TaskActor actor,
        TaskRequest request, long expectedSecurityRevision, CancellationToken ct = default)
    {
        try
        {
            var prepared = await PrepareAsync(tenant, sourceSession, actor, request, expectedSecurityRevision, ct);
            return prepared.Replay ?? await tasks.AdmitTaskAsync(prepared.Admission ?? throw new InvalidOperationException("Task preparation is missing."), ct);
        }
        catch (AccessDeniedException error) { AgentCore.Application.Observability.TaskTelemetry.Refusal(error.ReasonCode); throw; }
        catch (AgentCore.Application.Sessions.AgentCoreException error) { AgentCore.Application.Observability.TaskTelemetry.Refusal(error.ValidationCode ?? error.Code); throw; }
    }
    // Preparation writes no Task or worker graph. Only the owning transaction may
    // turn these exact selected identities and pins into admitted work.
    public ValueTask<TaskPreparation> PrepareAsync(Guid tenant, Guid sourceSession, TaskActor actor,
        TaskRequest request, long expectedSecurityRevision, CancellationToken ct = default)
        => PrepareCoreAsync(tenant, sourceSession, actor, request, expectedSecurityRevision, TaskPreparationPurpose.Direct, ct);
    public ValueTask<TaskPreparation> PrepareConsentAsync(Guid tenant, Guid sourceSession, TaskActor actor,
        TaskRequest request, long expectedSecurityRevision, CancellationToken ct = default)
        => PrepareCoreAsync(tenant, sourceSession, actor, request, expectedSecurityRevision, TaskPreparationPurpose.Consent, ct);
    private async ValueTask<TaskPreparation> PrepareCoreAsync(Guid tenant, Guid sourceSession, TaskActor actor,
        TaskRequest request, long expectedSecurityRevision, TaskPreparationPurpose purpose, CancellationToken ct)
    {
        actor.Validate();
        if (actor.Agent is not null && actor.Tool != "tasks.delegate") throw new AccessDeniedException("task_tool_unavailable");
        var state = await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var parent = await sessions.FindTenantSessionAsync(tenant, sourceSession, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var (sourceAgent, current) = await configurations.CurrentPolicyAsync(tenant, parent.Owner.AgentInstanceId, ct);
        var sourceRun = actor.Agent is { } frame ? await runs.FindTenantRunAsync(tenant, frame.RunId, ct) : null;
        var at = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        await TaskAuthority.SourceAsync(state, actor, parent, sourceAgent, sourceRun, current, iam, at, TaskAuthority.PreparationPermission(actor, purpose), true, ct);
        if (await tasks.FindTaskRequestAsync(tenant, sourceSession, actor, request.RequestKey, ct, purpose) is { } saved)
        {
            if (saved.Task.Request.Fingerprint() != request.Fingerprint()) throw new SecurityConflictException("task_request_conflict");
            AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.Replayed, saved.Task);
            return new(null, saved with { Replayed = true });
        }
        if (state.Tenant.Revision != expectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        var ancestor = sourceRun?.Admission.DelegatedTask is { } work ? await tasks.FindTaskAsync(tenant, work.TaskId, ct) : null;
        if (sourceRun?.Admission.DelegatedTask is not null && ancestor is null) throw new AccessDeniedException("task_parent_unavailable");
        var catalog = await models.ReadAsync(state, ct);
        var capacity = await tasks.ReadTaskCapacityAsync(tenant, request.CandidateInstanceIds, ct);
        var capacityUnavailable = false;
        var selections = new List<(TenantAgent Agent, ResolvedAgentRunConfiguration Configuration, AgentRunModelPin Model, int Fit, int Remaining)>();
        foreach (var id in request.CandidateInstanceIds)
        {
            try
            {
                var (target, _) = await configurations.CurrentPolicyAsync(tenant, id, ct);
                await TaskAuthority.TargetAsync(state, actor.PrincipalId, target, iam, ct);
                var remaining = target.Instance.Cooperation!.MaxConcurrentTasks - capacity[id];
                if (remaining <= 0) throw TaskAuthority.CapacityExceeded();
                var space = request.SpaceId is { } selected ? await spaces.FindSpaceAsync(tenant, selected, ct)
                    : await spaces.FindPrivateSpaceAsync(tenant, id, ct);
                if (space is null || space.DeletedAt is not null) throw new AccessDeniedException("space_unavailable");
                var resolved = await configurations.ResolveInSpaceAsync(tenant, id,
                    new(tenant, space.SpaceId, request.EnvironmentId, request.EnvironmentId is null ? 0 : space.EnvironmentSelectionEpoch, 1, at), ct);
                var preference = target.Instance.UnattendedModel ?? ModelPreference.Inherit;
                var defaults = resolved.Configuration.Configuration.Definition.ModelDefaults;
                var selection = routing is null ? SessionModelBinder.BindPreference(catalog, preference, defaults)
                    : await routing.ResolveAsync(catalog, preference, defaults, false, true, "delegatedTask",
                        RoutingTaskSignals.Project(null, "delegatedTask"), ct);
                var model = new AgentRunModelPin(selection.OfferingId, selection.ConnectionId, selection.ExternalModelId, selection.ReasoningEffort, selection.Binding, selection.Decision);
                if (!ExecutionModelPolicy.Matches(catalog, new(model.OfferingId, model.ConnectionId, model.ExternalModelId, model.ReasoningEffort,
                    ExecutionModelSource.ConversationDefault, model.Binding, model.Decision), out _)) throw new AccessDeniedException("target_model_unavailable");
                // Deterministic advisory fit uses declared role/skill labels only. It never changes eligibility.
                var labels = $"{target.Instance.Persona.Role} {target.Instance.Persona.Description} {string.Join(' ', resolved.Configuration.Skills.Select(s => s.Key))}";
                var words = labels.Split([' ', ',', '.', ':', '-', '_', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(w => w.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase);
                var fit = words.Count(w => request.Objective.Contains(w, StringComparison.OrdinalIgnoreCase));
                selections.Add((target, resolved.Configuration, model, fit, remaining));
            }
            catch (AccessDeniedException) when (request.Eligible) { }
            catch (AgentCore.Application.Sessions.AgentCoreException error) when (request.Eligible && error.Code == "TaskCapacityExceeded") { capacityUnavailable = true; }
            catch (AgentCore.Application.Sessions.AgentCoreException error) when (request.Eligible && error.Code is "ModelUnavailable" or "ModelRouteStale" or "ModelCapabilityUnsupported" or "ModelRoutingUnavailable") { }
        }
        var chosen = selections.OrderByDescending(s => s.Fit).ThenByDescending(s => s.Remaining).ThenBy(s => s.Agent.Instance.InstanceId).FirstOrDefault();
        if (chosen.Agent is null) { if (capacityUnavailable) throw TaskAuthority.CapacityExceeded(); throw new AccessDeniedException("task_target_unavailable"); }
        var taskId = ids.NewId(); var sessionId = ids.NewSessionId(); var activationId = ids.NewId(); var runId = ids.NewId();
        var source = TaskAuthority.Source(parent, actor, sourceRun, ancestor, activationId, ids.NewId());
        var deadline = request.DeadlineAt ?? (ancestor is null ? at.Add(TaskLimits.MaxLifetime) : ancestor.DeadlineAt);
        var allocation = await tasks.ProposeTaskBudgetAsync(tenant, sourceSession, actor,
            ExecutionBudgetResolver.Resolve(chosen.Configuration.Configuration.Definition, chosen.Configuration.InstanceBudgets, false).Profile, ct, purpose, request.ReportOnCompletion);
        var assignment = new TaskAssignment(chosen.Agent.Instance.InstanceId, chosen.Agent.PrincipalId,
            chosen.Configuration.Configuration.SpaceContext!.SpaceId, request.EnvironmentId, chosen.Agent.Instance.Revision,
            TaskLimits.Hash(chosen.Configuration.Configuration), request.Eligible ? "eligible-fit-capacity-id-v1" : "explicit", chosen.Model, chosen.Configuration.Configuration.SpaceContext.EnvironmentSelectionEpoch, chosen.Agent.Instance.Persona.Name, allocation);
        var pending = request.DependsOnTaskIds.Count > 0;
        var task = new DelegatedTask(taskId, source, request, assignment, pending ? DelegatedTaskState.PendingDependencies : DelegatedTaskState.Queued,
            1, at, at, deadline, pending ? null : sessionId, pending ? null : runId);
        var graph = pending ? ((SessionSnapshot Session, AgentRun Run)?)null
            : TaskAdmissionFactory.Worker(task, chosen.Agent, chosen.Configuration, chosen.Model, sessionId, ids.NewId(), activationId, runId, ids.NewId(), at);
        return new(new(tenant, sourceSession, actor, request, task, graph?.Session, graph?.Run,
            expectedSecurityRevision, ids.NewId()), null);
    }
}
