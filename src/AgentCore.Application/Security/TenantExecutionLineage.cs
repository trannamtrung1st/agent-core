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
    private static async ValueTask RequireOccurrenceRootAsync(AgentRun root, Func<Guid, CancellationToken, ValueTask<AgentCore.Domain.Triggers.TriggerOccurrence?>> occurrenceReader, CancellationToken ct)
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
                : ValueTask.FromException<AgentCore.Domain.Triggers.TriggerOccurrence?>(new AccessDeniedException("tenant_lineage_unavailable")), ct);

    // Owning transactions can read the same stored graph without opening a second context.
    public static async ValueTask<AgentRun> RootAsync(AgentRun run,
        Func<Guid, CancellationToken, ValueTask<AgentRun?>> runReader,
        Func<Guid, CancellationToken, ValueTask<AgentCore.Domain.Triggers.TriggerOccurrence?>> occurrenceReader, CancellationToken ct)
    {
        var initial = run.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_context_required");
        var visited = new HashSet<Guid>();
        for (var depth = 0; depth < 64; depth++)
        {
            var context = run.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_lineage_unavailable");
            if (!visited.Add(run.AgentRunId) || run.Owner.TenantId != initial.TenantId || run.Owner.ExecutorPrincipalId != initial.AgentPrincipalId
                || context.OriginalInitiatorPrincipalId != initial.OriginalInitiatorPrincipalId || context.RootActivationId != initial.RootActivationId)
                throw new AccessDeniedException("tenant_lineage_unavailable");
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
            if (context.Origin is not (ExecutionOrigin.ImmediateBackground or ExecutionOrigin.CompletionReport or ExecutionOrigin.ApprovalContinuation or ExecutionOrigin.ConfigurationContinuation or ExecutionOrigin.ContextContinuation or ExecutionOrigin.GoalContinuation)
                || context.ParentRunId is not { } parentId || context.ParentActivationId is not { } activationId)
                throw new AccessDeniedException("tenant_lineage_unavailable");
            var parent = await runReader(parentId, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("tenant_lineage_unavailable");
            var a = run.Admission.Activation;
            if (parent.Owner != run.Owner || parent.ActivationId != activationId || a.ParentActivationId != activationId
                || a.SourceAgentRunId != parentId || a.SourceSessionId != parent.SessionId)
                throw new AccessDeniedException("tenant_lineage_unavailable");
            if (context.Origin == ExecutionOrigin.ImmediateBackground
                && (a.Kind != ActivationKind.ImmediateBackground || !TenantBackgroundCreationRules.Allows(parent)
                    || run.SessionId == parent.SessionId || run.Admission.OutputContract != AgentRunOutputContract.BackgroundOutcome
                    || run.Admission.TaskIntent?.Source != AgentRunTaskSource.AgentRun || run.Admission.TaskIntent.SourceRevision != parent.AgentRunId.ToString("D")))
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
                && (a.Kind != ActivationKind.ApprovalContinuation || TenantConfigurationContinuationRules.Target(run) != TenantConfigurationContinuationRules.Target(parent)
                    || TenantContextContinuationRules.InputBoundary(run) != TenantContextContinuationRules.InputBoundary(parent)
                    || TenantConfigurationContinuationRules.InputBoundary(run) != TenantConfigurationContinuationRules.InputBoundary(parent) || parent.SessionId != run.SessionId || parent.Admission.OutputContract != run.Admission.OutputContract
                    || parent.Result?.ContinuationRunId != run.AgentRunId || parent.Result.OutcomeKind != AgentRunOutcomeKind.Continued))
                throw new AccessDeniedException("tenant_lineage_unavailable");
            run = parent;
        }
        throw new AccessDeniedException("tenant_lineage_unavailable");
    }
}
