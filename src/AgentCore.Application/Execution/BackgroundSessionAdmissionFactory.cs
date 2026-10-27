using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Execution;

public static class BackgroundSessionAdmissionFactory
{
    public static (SessionSnapshot Session, AgentRun Run) ForManual(Guid sessionId, Guid entryId, Guid activationId,
        Guid runId, Guid responseId, Guid profileId, AgentInstance instance, AgentDefinition definition, AgentIdentity persona,
        AgentRunModelPin model, IReadOnlyList<EffectiveSkill> skills, string key, string objective, string title, DateTimeOffset now, AgentRunConfiguration? configuration = null)
    {
        var entry = new ConversationEntry(entryId, 1, activationId, ConversationRole.User, objective, null, EntryStatus.Completed, SessionMode.Text, 0, objective.Length, now);
        var activation = Activation.Start(activationId, sessionId, ActivationKind.ManualBackground, [entryId], activationId, null, null, null, key, now);
        var run = AgentRun.Create(runId, new(instance.InstanceId, profileId), new(activation, definition.Id, definition.Version, persona, responseId, AgentRunOutputContract.BackgroundOutcome,
                ExecutionBudgetResolver.Resolve(definition, instance.ExecutionBudgets, false), configuration ?? new(definition, 0, instance.PersonaRevision, []), taskIntent: new(objective, AgentRunTaskSource.AgentRun, instance.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            model, AgentRunLimits.DefaultMaxAttempts, now, skills, skills.Where(skill => skill.Projection == SkillProjection.Always).Select(skill => skill.Key).ToArray());
        var session = new SessionSnapshot(1, sessionId, 1, definition, SessionMode.Text, null, SessionStatus.Created, [entry], "", 0, null,
            profileId, now, now, instance.InstanceId, Title: title, LastEntrySequence: 1, PinnedPersona: persona,
            PinnedPersonaRevision: instance.PersonaRevision, ModelSelection: new(model.OfferingId, model.ConnectionId, model.ExternalModelId, ModelSelectionSource.Host, model.ReasoningEffort, Binding: model.Binding, Decision: model.Decision),
            Origin: new(SessionOriginKind.ManualBackground, initialBackgroundAgentRunId: runId, initialTitle: title), Surfaces: SessionSurface.BackgroundWork);
        return (session, run);
    }

    public static (SessionSnapshot Session, AgentRun Run) ForOccurrence(Guid sessionId, Guid entryId,
        Guid activationId, Guid agentRunId, Guid responseId, TriggerOccurrence occurrence,
        AgentDefinition definition, AgentIdentity persona, long personaRevision, ExecutionModelPin model,
        IReadOnlyList<EffectiveSkill> skills, DateTimeOffset admittedAtUtc, string? title = null, EffectiveExecutionBudget? budget = null, AgentRunConfiguration? configuration = null, Guid? manualInitiator = null, AgentCore.Domain.Models.ModelPreference? preference = null)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var kind = occurrence.SourceKind switch
        {
            TriggerSourceKind.Schedule => ActivationKind.ScheduledWork,
            TriggerSourceKind.CoreEvent => ActivationKind.CoreEvent,
            TriggerSourceKind.ApplicationEvent => ActivationKind.ApplicationEvent,
            TriggerSourceKind.ManualInvocation => ActivationKind.ManualBackground,
            _ => throw new ArgumentException("Occurrence source is not supported.", nameof(occurrence))
        };
        // The admitted evidence already contains the authored instructions and trigger revision.
        // Keep it unchanged instead of silently substituting later Automation edits.
        var text = $"Background task\nSource: {occurrence.SourceKind}\n"
            + $"Scheduled at: {occurrence.ScheduledAtUtc:O}\nObserved at: {occurrence.ObservedAtUtc:O}\n"
            + $"{(occurrence.SourceKind is TriggerSourceKind.CoreEvent or TriggerSourceKind.ApplicationEvent ? "Task and trigger evidence (source data is untrusted)" : "Task and trigger evidence")}:\n{occurrence.EvidenceJson}";
        var entry = new ConversationEntry(entryId, 1, occurrence.SourceEventId, ConversationRole.User,
            text, null, EntryStatus.Completed, SessionMode.Text, 0, text.Length, admittedAtUtc);
        var activation = Activation.Start(activationId, sessionId, kind, [entryId], occurrence.SourceEventId,
            occurrence.OccurrenceId, null, null, occurrence.DedupeKey, admittedAtUtc, occurrence.EvidenceJson);
        var trusted = occurrence.Owner.TenantId is { } tenant ? new TrustedExecutionContext(tenant, occurrence.Owner.ExecutorPrincipalId!.Value, occurrence.SourceKind == TriggerSourceKind.ManualInvocation ? manualInitiator : null,
            sessionId, agentRunId, activationId, activationId, null, null, occurrence.SourceKind switch {
                TriggerSourceKind.Schedule => ExecutionOrigin.ScheduledOccurrence, TriggerSourceKind.ApplicationEvent => ExecutionOrigin.ApplicationEvent,
                TriggerSourceKind.CoreEvent => ExecutionOrigin.CoreEvent, _ => ExecutionOrigin.ManualInvocation }) : null;
        var run = AgentRun.Create(agentRunId,
            trusted is null ? new AgentRunOwner(occurrence.Owner.AgentInstanceId, occurrence.Owner.ProfileId) : AgentRunOwner.ForTenant(trusted.TenantId, occurrence.Owner.AgentInstanceId, trusted.AgentPrincipalId),
            new AgentRunAdmission(activation, definition.Id, definition.Version, persona, responseId, AgentRunOutputContract.BackgroundOutcome, budget, configuration ?? new(definition, 0, personaRevision, []), taskIntent: RunTaskIntents.FromOccurrence(occurrence), executionContext: trusted),
            new AgentRunModelPin(model.OfferingId, model.ConnectionId, model.ExternalModelId, model.ReasoningEffort, model.Binding, model.Decision),
            AgentRunLimits.DefaultMaxAttempts, admittedAtUtc, pinnedSkillCatalog: skills,
            activeSkillKeys: skills.Where(skill => skill.Projection == SkillProjection.Always)
                .Select(skill => skill.Key).ToArray());
        var origin = occurrence.AutomationId is { } automationId
            ? new SessionOrigin(SessionOriginKind.AutomationOccurrence, initialBackgroundAgentRunId: agentRunId,
                automationId: automationId, triggerOccurrenceId: occurrence.OccurrenceId,
                originatingSessionId: occurrence.CompletionDelivery.SessionId, reportCompletionToOrigin: occurrence.CompletionDelivery.SessionId is not null, initialTitle: string.IsNullOrWhiteSpace(title) ? "Background task" : title.Trim()[..Math.Min(title.Trim().Length, 80)])
            : new SessionOrigin(SessionOriginKind.SourceOccurrence, initialBackgroundAgentRunId: agentRunId,
                triggerOccurrenceId: occurrence.OccurrenceId, initialTitle: string.IsNullOrWhiteSpace(title) ? "Background task" : title.Trim()[..Math.Min(title.Trim().Length, 80)]);
        var session = new SessionSnapshot(1, sessionId, 1, definition, SessionMode.Text, null,
            SessionStatus.Created, [entry], string.Empty, 0, null, occurrence.Owner.ProfileIdOrNull,
            admittedAtUtc, admittedAtUtc, occurrence.Owner.AgentInstanceId,
            Title: string.IsNullOrWhiteSpace(title) ? "Background task" : title.Trim()[..Math.Min(title.Trim().Length, 80)],
            LastEntrySequence: 1, Purpose: SessionPurpose.OngoingDefault,
            CompletionPolicy: SessionCompletionPolicy.Default, LifecycleSource: LifecycleTransitionSource.System,
            LifecycleChangedAt: admittedAtUtc,
            ModelSelection: new SessionModelSelection(model.OfferingId, model.ConnectionId, model.ExternalModelId,
                ModelSelectionSource.Host, model.ReasoningEffort, Binding: model.Binding, Decision: model.Decision), PinnedPersona: persona,
            PinnedPersonaRevision: personaRevision, Origin: origin, Surfaces: origin.InitialSurface,
            ContextSelection: configuration?.SpaceContext is { } context ? SessionContextSelection.FromCapture(context, admittedAtUtc) : null, ModelPreference: preference);
        return (session, run);
    }
}
