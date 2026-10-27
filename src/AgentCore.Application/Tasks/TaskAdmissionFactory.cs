using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Tasks;

public static class TaskAdmissionFactory
{
    public static void RequireHumanGraph(TenantHumanTaskAdmission admission, TenantSession source, TenantAgent worker, DateTimeOffset at)
    {
        var task = admission.Task; task.Source.Validate(); task.Assignment.Validate();
        if (task.ContentRetiredAt is not null || task.Request.RetiredFingerprint is not null) throw new ArgumentException("Retired content cannot authorize new work.");
        if (task.Source.ActorKind != TaskActorKind.Human || source.Owner.TenantId != task.Source.TenantId
            || source.Owner.SessionId != task.Source.SessionId || source.Owner.AgentInstanceId != task.Source.InstanceId
            || task.Revision != 1 || task.CreatedAt > at || task.Assignment.InstanceId != worker.Instance.InstanceId
            || task.Assignment.PrincipalId != worker.PrincipalId)
            throw new AgentCore.Application.Security.AccessDeniedException("task_admission_mismatch");
        if (task.State == DelegatedTaskState.PendingDependencies)
        {
            if (admission.Session is not null || admission.Run is not null || task.WorkerSessionId is not null || task.InitialWorkerRunId is not null)
                throw new AgentCore.Application.Security.AccessDeniedException("task_admission_mismatch");
            return;
        }
        var run = admission.Run ?? throw new AgentCore.Application.Security.AccessDeniedException("task_admission_mismatch");
        var session = admission.Session ?? throw new AgentCore.Application.Security.AccessDeniedException("task_admission_mismatch");
        if (task.State != DelegatedTaskState.Queued || !run.IsInitialQueued || run.CreatedAtUtc != task.CreatedAt
            || run.AgentRunId != task.InitialWorkerRunId || session.SessionId != task.WorkerSessionId || run.ApprovalRequestId is not null)
            throw new AgentCore.Application.Security.AccessDeniedException("task_admission_mismatch");
        var expected = Worker(task, worker, new(run.Admission.Configuration!, worker.Instance.Persona, run.PinnedSkillCatalog, worker.Instance.ExecutionBudgets),
            task.Assignment.Model!, session.SessionId, session.Entries.Single().EntryId, run.ActivationId, run.AgentRunId, run.ResponseId!.Value, task.CreatedAt);
        if (run.Owner != expected.Run.Owner || System.Text.Json.JsonSerializer.Serialize(run.Admission) != System.Text.Json.JsonSerializer.Serialize(expected.Run.Admission)
            || System.Text.Json.JsonSerializer.Serialize(session) != System.Text.Json.JsonSerializer.Serialize(expected.Session))
            throw new AgentCore.Application.Security.AccessDeniedException("task_admission_mismatch");
    }
    public static (SessionSnapshot Session, AgentRun Run) Worker(DelegatedTask task, TenantAgent worker,
        ResolvedAgentRunConfiguration resolved, AgentRunModelPin model, Guid sessionId, Guid entryId, Guid activationId,
        Guid runId, Guid responseId, DateTimeOffset at)
    {
        var source = task.Source; var configuration = resolved.Configuration;
        if (worker.TenantId != source.TenantId || worker.Instance.InstanceId != task.Assignment.InstanceId || worker.PrincipalId != task.Assignment.PrincipalId
            || task.Assignment.Model is null || System.Text.Json.JsonSerializer.Serialize(task.Assignment.Model) != System.Text.Json.JsonSerializer.Serialize(model)
            || task.Assignment.ConfigurationHash != TaskLimits.Hash(configuration) || task.Assignment.InstanceRevision != worker.Instance.Revision
            || configuration.SpaceContext is not { } space || space.SpaceId != task.Assignment.SpaceId || space.EnvironmentId != task.Assignment.EnvironmentId)
            throw new ArgumentException("Worker configuration must match the exact task assignment.");
        var text = task.Request.Objective;
        var entry = new ConversationEntry(entryId, 1, source.ActivationId ?? source.HumanCommandId, ConversationRole.User,
            text, null, EntryStatus.Completed, SessionMode.Text, 0, text.Length, at);
        var activation = new Activation(activationId, sessionId, ActivationKind.DelegatedTask, [entryId], entry.SourceEventId,
            null, source.RunId is null ? null : source.SessionId, source.RunId, $"task:{task.TaskId:D}", at,
            source.RootActivationId, source.ActivationId, System.Text.Json.JsonSerializer.Serialize(new { taskId = task.TaskId, assignmentHash = TaskLimits.Hash(task.Assignment) }));
        var context = new TrustedExecutionContext(source.TenantId, worker.PrincipalId, source.OriginalInitiatorPrincipalId,
            sessionId, runId, activationId, source.RootActivationId, source.RunId, source.ActivationId, ExecutionOrigin.DelegatedTask);
        var run = AgentRun.Create(runId, AgentRunOwner.ForTenant(source.TenantId, worker.Instance.InstanceId, worker.PrincipalId),
            new(activation, configuration.Definition.Id, configuration.Definition.Version, resolved.Persona, responseId,
                AgentRunOutputContract.BackgroundOutcome, TaskBudgetRules.Worker(configuration.Definition, resolved.InstanceBudgets, task.Assignment.Budget ?? throw new ArgumentException("Task budget reservation is required.")), configuration,
                taskIntent: new(text, AgentRunTaskSource.DelegatedTask, task.TaskId.ToString("D")), executionContext: context,
                delegatedTask: new(task.TaskId, task.Revision, TaskLimits.Hash(task.Assignment))), model, AgentRunLimits.DefaultMaxAttempts, at,
            resolved.Skills, resolved.Skills.Where(s => s.Projection == SkillProjection.Always).Select(s => s.Key).ToArray());
        var origin = new SessionOrigin(SessionOriginKind.DelegatedTask, source.SessionId, source.RunId, runId,
            reportCompletionToOrigin: false, initialTitle: task.Request.Title);
        var session = new SessionSnapshot(1, sessionId, 1, configuration.Definition, SessionMode.Text, null, SessionStatus.Created,
            [entry], "", 0, null, null, at, at, worker.Instance.InstanceId, Title: task.Request.Title, LastEntrySequence: 1,
            PinnedPersona: resolved.Persona, PinnedPersonaRevision: configuration.PersonaRevision,
            ModelSelection: new(model.OfferingId, model.ConnectionId, model.ExternalModelId, ModelSelectionSource.Host, model.ReasoningEffort, model.Binding, model.Decision),
            Origin: origin, Surfaces: SessionSurface.BackgroundWork, ContextSelection: AgentCore.Domain.Spaces.SessionContextSelection.FromCapture(space, at),
            ModelPreference: worker.Instance.UnattendedModel);
        return (session, run);
    }
    public static void RequireGraph(TaskAdmission admission, TenantSession parent, AgentRun? source, TenantAgent worker, DateTimeOffset at)
    {
        var task = admission.Task;
        if (task.ContentRetiredAt is not null || task.Request.RetiredFingerprint is not null) throw new ArgumentException("Retired content cannot authorize new work.");
        if (task.Source.TenantId != admission.TenantId || task.Source.SessionId != admission.SourceSessionId || task.Source.PrincipalId != admission.Actor.PrincipalId
            || task.Request.Fingerprint() != admission.Request.Fingerprint() || task.Request.RequestKey != admission.Request.RequestKey
            || task.CreatedAt > at || task.Source.InstanceId != parent.Owner.AgentInstanceId || task.Revision != 1
            || task.Assignment.InstanceId != worker.Instance.InstanceId || task.Assignment.PrincipalId != worker.PrincipalId
            || task.Source.ActorKind != (admission.Actor.Human is null ? TaskActorKind.Agent : TaskActorKind.Human)
            || source is not null && (task.Source.RunId != source.AgentRunId || task.Source.ActivationId != source.ActivationId
                || task.Source.RootActivationId != source.Admission.Activation.RootActivationId || task.Source.OriginalInitiatorPrincipalId != source.Admission.ExecutionContext?.OriginalInitiatorPrincipalId)
            || source is null && (task.Source.RunId is not null || task.Source.ActivationId is not null))
            throw new AgentCore.Application.Security.AccessDeniedException("task_admission_mismatch");
        if (task.State == DelegatedTaskState.PendingDependencies)
        {
            if (admission.WorkerSession is not null || admission.WorkerRun is not null || task.WorkerSessionId is not null)
                throw new AgentCore.Application.Security.AccessDeniedException("task_admission_mismatch");
            return;
        }
        var run = admission.WorkerRun ?? throw new ArgumentException("Task worker Run is required.");
        var session = admission.WorkerSession ?? throw new ArgumentException("Task worker Session is required.");
        if (task.State != DelegatedTaskState.Queued || task.WorkerSessionId != session.SessionId || task.InitialWorkerRunId != run.AgentRunId
            || !run.IsInitialQueued || run.CreatedAtUtc != task.CreatedAt || session.CreatedAt != task.CreatedAt || run.ApprovalRequestId is not null)
            throw new AgentCore.Application.Security.AccessDeniedException("task_admission_mismatch");
        var resolved = new ResolvedAgentRunConfiguration(run.Admission.Configuration!, worker.Instance.Persona, run.PinnedSkillCatalog, worker.Instance.ExecutionBudgets);
        var expected = Worker(task, worker, resolved, run.PinnedModel, session.SessionId, session.Entries.Single().EntryId,
            run.ActivationId, run.AgentRunId, run.ResponseId!.Value, task.CreatedAt);
        if (System.Text.Json.JsonSerializer.Serialize(expected.Run.Admission) != System.Text.Json.JsonSerializer.Serialize(run.Admission)
            || System.Text.Json.JsonSerializer.Serialize(expected.Session) != System.Text.Json.JsonSerializer.Serialize(session))
            throw new AgentCore.Application.Security.AccessDeniedException("task_admission_mismatch");
    }
}
