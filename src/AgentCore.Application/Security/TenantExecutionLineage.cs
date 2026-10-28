using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Security;

// Stored lineage is attribution and causal fencing, never a permission snapshot.
public static class TenantExecutionLineage
{
    public static async ValueTask RequireRequesterAsync(AgentRun run, TenantSecurityState state, IamAuthorizer iam, ITenantRunStore runs, CancellationToken ct)
    {
        var root = await RootAsync(run, runs, ct).ConfigureAwait(false);
        await AgentCore.Application.Tasks.TaskReadReferences.RequireAsync(run, state, iam, runs, ct).ConfigureAwait(false);
        await AgentCore.Application.Workflows.WorkflowReadReferences.RequireAsync(run, state, iam, runs, ct).ConfigureAwait(false);
        if (run.Admission.AgentMessage is { } messageWork)
        {
            var message = runs is ITenantAgentMessageStore inbox ? await inbox.FindAgentMessageSourceAsync(state.Tenant.TenantId, messageWork.MessageId, ct) : null;
            if (message is null) throw new AccessDeniedException("message_source_unavailable");
            await ((ITenantAgentMessageStore)runs).RequireAgentMessageExecutionSourcesAsync(run, ct);
            if (run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is { } human)
                foreach (var key in new[] { "sessions.read", "agent-messages.send" })
                    await AgentCore.Application.Tasks.TaskAuthority.RequireAsync(state, human, key,
                        TenantSessionRules.Scope(state.Tenant.TenantId, message.Message.SourceSessionId!.Value), iam, ct);
            return;
        }
        if (run.Admission.TaskReport is not null)
        {
            if (runs is not ITenantTaskStore taskReports) throw new AccessDeniedException("task_report_unavailable");
            await taskReports.RequireTaskReportSourcesAsync(run, ct); return;
        }
        if (run.Admission.DelegatedTask is { } work)
        {
            var task = runs is ITenantTaskStore tasks ? await tasks.FindTaskAsync(state.Tenant.TenantId, work.TaskId, ct).ConfigureAwait(false) : null;
            if (task is null) throw new AccessDeniedException("task_lineage_unavailable");
            await ((ITenantTaskStore)runs).RequireTaskExecutionSourcesAsync(run, ct);
            return;
        }
        if (run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is not { } requester) return;
        if (root.Admission.ExecutionContext?.Origin == ExecutionOrigin.ManualInvocation)
        {
            var source = TenantAutomationRules.Scope(state.Tenant.TenantId, TenantAutomationRules.SourceAutomation(root));
            foreach (var key in new[] { "automations.read", "automations.run" })
                if (!(await iam.CheckTenantAsync(state, requester, PermissionCatalog.Get(key), source, ct).ConfigureAwait(false)).Allowed)
                    throw new AccessDeniedException("requester_unavailable");
            return;
        }
        foreach (var (key, scope) in new[] { ("sessions.read", TenantSessionRules.Scope(state.Tenant.TenantId, root.SessionId)),
            ("agents.chat", AccessScope.ForResource(state.Tenant.TenantId, "agent", run.AgentInstanceId.ToString("D"))) })
            if (!(await iam.CheckOwnedTenantAsync(state, requester, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("requester_unavailable");
    }
    private static async ValueTask RequireOccurrenceRootAsync(AgentRun root, Func<Guid, CancellationToken, ValueTask<AgentCore.Domain.Triggers.TriggerOccurrence?>> occurrenceReader, CancellationToken ct,
        Func<Guid, CancellationToken, ValueTask<DelegatedTask?>>? taskReader = null)
    {
        var context = root.Admission.ExecutionContext!;
        if (root.Admission.Activation.TriggerOccurrenceId is not { } id)
            throw new AccessDeniedException("tenant_lineage_unavailable");
        var occurrence = await occurrenceReader(id, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("tenant_lineage_unavailable");
        if (occurrence.Owner != AgentCore.Domain.Triggers.TriggerOwner.ForTenant(context.TenantId, root.AgentInstanceId, context.AgentPrincipalId)
            || occurrence.Disposition != AgentCore.Domain.Triggers.OccurrenceRoutingDisposition.AcceptedDurable
            || occurrence.AcceptedAgentRunId != root.AgentRunId || occurrence.ExecutionSessionId != root.SessionId
            || occurrence.AutomationId != TenantAutomationRules.SourceAutomation(root)
            || occurrence.SourceKind != (context.Origin switch {
                ExecutionOrigin.ScheduledOccurrence => AgentCore.Domain.Triggers.TriggerSourceKind.Schedule,
                ExecutionOrigin.ManualInvocation => AgentCore.Domain.Triggers.TriggerSourceKind.ManualInvocation,
                ExecutionOrigin.ApplicationEvent => AgentCore.Domain.Triggers.TriggerSourceKind.ApplicationEvent,
                ExecutionOrigin.CoreEvent => AgentCore.Domain.Triggers.TriggerSourceKind.CoreEvent,
                _ => throw new AccessDeniedException("tenant_lineage_unavailable") }))
            throw new AccessDeniedException("tenant_lineage_unavailable");
        var eventKind = occurrence.SourceKind is AgentCore.Domain.Triggers.TriggerSourceKind.CoreEvent or AgentCore.Domain.Triggers.TriggerSourceKind.ApplicationEvent
            ? occurrence.SourceKind : (AgentCore.Domain.Triggers.TriggerSourceKind?)null;
        if (root.Admission.TaskIntent?.EventSourceKind != eventKind) throw new AccessDeniedException("tenant_lineage_unavailable");
        using var evidence = System.Text.Json.JsonDocument.Parse(occurrence.EvidenceJson);
        if (!evidence.RootElement.TryGetProperty("automationRevision", out var revision) || !revision.TryGetInt64(out var version)
            || root.Admission.TaskIntent?.SourceRevision != $"{occurrence.AutomationId:D}:{version}"
            || !evidence.RootElement.TryGetProperty("instructions", out var instructions) || instructions.GetString() != root.Admission.TaskIntent.Text)
            throw new AccessDeniedException("tenant_lineage_unavailable");
    }
    public static ValueTask<AgentRun> RootAsync(AgentRun run, ITenantRunStore runs, CancellationToken ct) =>
        RootAsync(run, (id, token) => runs.FindTenantRunAsync(run.Owner.TenantId!.Value, id, token),
            (id, token) => runs is ITenantAutomationStore automations
                ? automations.FindTenantOccurrenceAsync(run.Owner.TenantId!.Value, id, token)
                : ValueTask.FromException<AgentCore.Domain.Triggers.TriggerOccurrence?>(new AccessDeniedException("tenant_lineage_unavailable")), ct,
            runs is ITenantTaskStore tasks ? (id, token) => tasks.FindTaskAsync(run.Owner.TenantId!.Value, id, token) : null,
            runs is ITenantAgentMessageStore messages ? (id, token) => messages.FindAgentMessageSourceAsync(run.Owner.TenantId!.Value, id, token) : null);

    // Owning transactions can read the same stored graph without opening a second context.
    public static async ValueTask<AgentRun> RootAsync(AgentRun run,
        Func<Guid, CancellationToken, ValueTask<AgentRun?>> runReader,
        Func<Guid, CancellationToken, ValueTask<AgentCore.Domain.Triggers.TriggerOccurrence?>> occurrenceReader, CancellationToken ct,
        Func<Guid, CancellationToken, ValueTask<DelegatedTask?>>? taskReader = null,
        Func<Guid, CancellationToken, ValueTask<AgentMessageResult?>>? messageReader = null)
    {
        var initial = run.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_context_required");
        var visited = new HashSet<Guid>();
        for (var depth = 0; depth < 64; depth++)
        {
            var context = run.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_lineage_unavailable");
            if (!visited.Add(run.AgentRunId) || run.Owner.TenantId != initial.TenantId || run.Owner.ExecutorPrincipalId != context.AgentPrincipalId
                || context.OriginalInitiatorPrincipalId != initial.OriginalInitiatorPrincipalId || context.RootActivationId != initial.RootActivationId)
                throw new AccessDeniedException("tenant_lineage_unavailable");
            if (context.Origin == ExecutionOrigin.AgentMessage)
            {
                var work = run.Admission.AgentMessage ?? throw new AccessDeniedException("message_lineage_unavailable");
                var source = messageReader is null ? null : await messageReader(work.MessageId, ct);
                if (source is null || source.Thread.TaskId is not null || source.Thread.ThreadId != work.ThreadId
                    || source.Message.RecipientInstanceId != run.AgentInstanceId || source.Receipt.WakeRunId != run.AgentRunId
                    || source.Receipt.WakeSessionId != run.SessionId || source.Receipt.WakeRootActivationId != work.WakeRootActivationId
                    || source.Receipt.WakeHops != work.WakeHops || context.ParentRunId is not null || context.ParentActivationId is not null
                    || context.RootActivationId != run.ActivationId || run.Admission.Activation.Kind != ActivationKind.AgentMessage
                    || run.Admission.Activation.SourceEventId != work.MessageId || run.Admission.Activation.DedupeKey != $"agent-message:{work.MessageId:D}")
                    throw new AccessDeniedException("message_lineage_unavailable");
                return run;
            }
            if (context.Origin == ExecutionOrigin.TaskCompletion)
            {
                var work = run.Admission.TaskReport ?? throw new AccessDeniedException("task_report_unavailable");
                var task = taskReader is null ? null : await taskReader(work.TaskId, ct);
                var worker = context.ParentRunId is { } workerId ? await runReader(workerId, ct) : null;
                var activation = run.Admission.Activation;
                if (task is null || worker is null || !task.Request.ReportOnCompletion || !TaskLimits.Terminal(task.State)
                    || work.TaskRevision != task.Revision || work.TerminalRunId != task.TerminalRunId || task.TerminalRunId != worker.AgentRunId
                    || task.Source.SessionId != run.SessionId || task.Source.InstanceId != run.AgentInstanceId
                    || task.WorkerSessionId != worker.SessionId || task.Assignment.InstanceId != worker.AgentInstanceId
                    || task.Assignment.PrincipalId != worker.Owner.ExecutorPrincipalId || worker.ActivationId != context.ParentActivationId
                    || activation.Kind != ActivationKind.TaskCompletion || activation.SourceEventId != work.NotificationId
                    || activation.SourceAgentRunId != worker.AgentRunId || activation.SourceSessionId != worker.SessionId
                    || activation.ParentActivationId != worker.ActivationId || activation.DedupeKey != $"task-completion:{work.NotificationId:D}")
                    throw new AccessDeniedException("task_report_unavailable");
                run = worker; continue;
            }
            if (context.Origin == ExecutionOrigin.DelegatedTask)
            {
                var witness = run.Admission.DelegatedTask ?? throw new AccessDeniedException("task_lineage_unavailable");
                var task = taskReader is null ? null : await taskReader(witness.TaskId, ct).ConfigureAwait(false);
                if (task is null || task.Source.TenantId != initial.TenantId || task.WorkerSessionId != run.SessionId || task.InitialWorkerRunId != run.AgentRunId
                    || task.Assignment.PrincipalId != context.AgentPrincipalId || task.Assignment.InstanceId != run.AgentInstanceId
                    || task.Source.RootActivationId != context.RootActivationId || task.Source.RunId != context.ParentRunId
                    || task.Source.ActivationId != context.ParentActivationId || task.Source.OriginalInitiatorPrincipalId != context.OriginalInitiatorPrincipalId
                    || witness.AssignmentHash != TaskLimits.Hash(task.Assignment) || run.Admission.Activation.Kind != ActivationKind.DelegatedTask
                    || run.Admission.TaskIntent?.SourceRevision != task.TaskId.ToString("D")) throw new AccessDeniedException("task_lineage_unavailable");
                if (task.Source.ActorKind == TaskActorKind.Human)
                {
                    if (task.Source.HumanCommandId is null || context.ParentRunId is not null || task.Source.RootActivationId != run.ActivationId)
                        throw new AccessDeniedException("task_lineage_unavailable");
                    return run;
                }
                if (task.Source.ActorKind == TaskActorKind.WorkflowContinuation)
                {
                    if (task.Source.Workflow is not { } process || task.Source.SessionId is not null || context.ParentRunId is not null
                        || context.ParentActivationId is not null || task.Source.RootActivationId != run.ActivationId
                        || run.Admission.Activation.SourceEventId != process.OperationId)
                        throw new AccessDeniedException("workflow_task_lineage_unavailable");
                    // This is the actual independently admitted worker root. Original process/source
                    // lineage lives in its immutable workflow witness; no source Run is invented.
                    return run;
                }
                var source = await runReader(task.Source.RunId!.Value, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("task_lineage_unavailable");
                if (source.SessionId != task.Source.SessionId || source.AgentInstanceId != task.Source.InstanceId
                    || source.Owner.ExecutorPrincipalId != task.Source.PrincipalId || source.ActivationId != task.Source.ActivationId)
                    throw new AccessDeniedException("task_lineage_unavailable");
                run = source; continue;
            }
            if (context.Origin is ExecutionOrigin.ScheduledOccurrence or ExecutionOrigin.ManualInvocation or ExecutionOrigin.ApplicationEvent or ExecutionOrigin.CoreEvent)
            {
                var expected = context.Origin switch { ExecutionOrigin.ScheduledOccurrence => ActivationKind.ScheduledWork,
                    ExecutionOrigin.ManualInvocation => ActivationKind.ManualBackground, ExecutionOrigin.ApplicationEvent => ActivationKind.ApplicationEvent, _ => ActivationKind.CoreEvent };
                if (context.Origin != ExecutionOrigin.ManualInvocation && context.OriginalInitiatorPrincipalId is not null || context.RootActivationId != run.ActivationId || context.ParentRunId is not null || context.ParentActivationId is not null
                    || run.Admission.Activation.Kind != expected || run.Admission.Activation.TriggerOccurrenceId is null || run.Admission.Activation.SourceAgentRunId is not null)
                    throw new AccessDeniedException("tenant_lineage_unavailable");
                await RequireOccurrenceRootAsync(run, occurrenceReader, ct).ConfigureAwait(false);
                return run;
            }
            if (context.Origin == ExecutionOrigin.UserMessage)
            {
                if (run.Admission.Activation.Kind != ActivationKind.UserTurn || context.RootActivationId != run.ActivationId
                    || context.ParentRunId is not null || context.ParentActivationId is not null)
                    throw new AccessDeniedException("tenant_lineage_unavailable");
                return run;
            }
            if (context.Origin is not (ExecutionOrigin.CompletionReport or ExecutionOrigin.ApprovalContinuation or ExecutionOrigin.ConfigurationContinuation or ExecutionOrigin.ContextContinuation or ExecutionOrigin.GoalContinuation)
                || context.ParentRunId is not { } parentId || context.ParentActivationId is not { } activationId)
                throw new AccessDeniedException("tenant_lineage_unavailable");
            var parent = await runReader(parentId, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("tenant_lineage_unavailable");
            var a = run.Admission.Activation;
            if (parent.Owner != run.Owner || parent.ActivationId != activationId || a.ParentActivationId != activationId
                || a.SourceAgentRunId != parentId || a.SourceSessionId != parent.SessionId)
                throw new AccessDeniedException("tenant_lineage_unavailable");
            if (context.Origin == ExecutionOrigin.CompletionReport
                && (a.Kind != ActivationKind.BackgroundCompleted || !parent.IsTerminal || parent.Admission.OutputContract != AgentRunOutputContract.BackgroundOutcome
                    || run.Admission.OutputContract != AgentRunOutputContract.CompletionReport))
                throw new AccessDeniedException("tenant_lineage_unavailable");
            if (context.Origin == ExecutionOrigin.CompletionReport)
            {
                var childRoot = await TenantBackgroundLineage.InitialAsync(parent, runReader, ct).ConfigureAwait(false);
                if (AgentCore.Application.Tools.TenantTaskExecutionContract.IsIndependent(childRoot))
                {
                    var occurrence = await occurrenceReader(childRoot.Admission.Activation.TriggerOccurrenceId!.Value, ct).ConfigureAwait(false);
                    TenantCompletionDestinationRules.Binding(childRoot, occurrence, run.SessionId);
                }
                else if (childRoot.Admission.Activation.SourceSessionId != run.SessionId) throw new AccessDeniedException("tenant_lineage_unavailable");
            }
            if (context.Origin == ExecutionOrigin.GoalContinuation
                && (a.Kind != ActivationKind.GoalContinuation || parent.Status != AgentRunStatus.Completed || parent.CancellationRequested
                    || parent.SessionId != run.SessionId || run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
                    || a.SourceEventId != parent.ActivationId || a.SourceEntryIds.Count != 0 || run.MaxAttempts != 1
                    || run.Admission.GoalWork is not { } goal || parent.Admission.GoalWork?.GoalId != goal.GoalId
                    || a.DedupeKey != $"goal:{parent.AgentRunId:N}:{goal.GoalId:N}:{goal.PlanRevision}")) throw new AccessDeniedException("tenant_lineage_unavailable");
            if (context.Origin == ExecutionOrigin.ContextContinuation
                && (a.Kind != ActivationKind.ContextContinuation || parent.Status != AgentRunStatus.Completed || parent.CancellationRequested
                    || parent.SessionId != run.SessionId || run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
                    || a.SourceEventId != parent.ActivationId || a.SourceEntryIds.Count != 0 || run.MaxAttempts != 1
                    || a.DedupeKey != "context-continuation:" + context.RootActivationId.ToString("D")
                    || TenantContextContinuationRules.Pending(parent) is not { } pending
                    || run.Admission.Configuration?.SpaceContext is not { } capture || capture.SpaceId != pending.SpaceId || capture.EnvironmentId != pending.EnvironmentId
                    || capture.SelectionRevision != pending.SelectionRevision)) throw new AccessDeniedException("tenant_lineage_unavailable");
            if (context.Origin == ExecutionOrigin.ConfigurationContinuation
                && (a.Kind != ActivationKind.ConfigurationContinuation || !parent.IsTerminal || parent.Status != AgentRunStatus.Completed
                    || parent.SessionId != run.SessionId || run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
                    || a.SourceEventId != parent.ActivationId || a.SourceEntryIds.Count != 0 || run.MaxAttempts != 1
                    || a.DedupeKey != "configuration-continuation:" + parent.AgentRunId.ToString("D") || TenantConfigurationContinuationRules.Target(run) is null))
                throw new AccessDeniedException("tenant_lineage_unavailable");
            if (context.Origin == ExecutionOrigin.ApprovalContinuation
                && (a.Kind != ActivationKind.ApprovalContinuation || run.Admission.AgentMessage != parent.Admission.AgentMessage || run.Admission.TaskReport != parent.Admission.TaskReport || TenantConfigurationContinuationRules.Target(run) != TenantConfigurationContinuationRules.Target(parent)
                    || TenantContextContinuationRules.InputBoundary(run) != TenantContextContinuationRules.InputBoundary(parent)
                    || TenantConfigurationContinuationRules.InputBoundary(run) != TenantConfigurationContinuationRules.InputBoundary(parent) || parent.SessionId != run.SessionId || parent.Admission.OutputContract != run.Admission.OutputContract
                    || parent.Result?.ContinuationRunId != run.AgentRunId || parent.Result.OutcomeKind != AgentRunOutcomeKind.Continued))
                throw new AccessDeniedException("tenant_lineage_unavailable");
            run = parent;
        }
        throw new AccessDeniedException("tenant_lineage_unavailable");
    }
}
