using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Execution;

public static class BackgroundSessionAdmissionFactory
{
    public static (SessionSnapshot Session, AgentRun Run) ForImmediate(Guid sessionId, Guid entryId,
        Guid activationId, Guid runId, Guid responseId, SessionSnapshot parent, AgentRun source,
        string toolCallId, string objective, string? title, bool reportCompletion, DateTimeOffset now)
    {
        var entry = new ConversationEntry(entryId, 1, source.Admission.Activation.SourceEventId, ConversationRole.User,
            objective, null, EntryStatus.Completed, SessionMode.Text, 0, objective.Length, now);
        var key = $"background:{toolCallId}";
        var activation = new Activation(activationId, sessionId, ActivationKind.ImmediateBackground, [entryId],
            entry.SourceEventId, null, parent.SessionId, source.AgentRunId, key, now);
        var run = AgentRun.Create(runId, source.Owner, new(activation, source.DefinitionId, source.DefinitionVersion,
            source.PinnedPersona, responseId, AgentRunOutputContract.BackgroundOutcome), source.PinnedModel, AgentRunLimits.DefaultMaxAttempts, now,
            source.PinnedSkillCatalog, source.PinnedSkillCatalog.Where(skill => skill.Projection == SkillProjection.Always).Select(skill => skill.Key).ToArray());
        var origin = new SessionOrigin(SessionOriginKind.ImmediateBackground, parent.SessionId, source.AgentRunId,
            runId, reportCompletionToOrigin: reportCompletion);
        var session = new SessionSnapshot(1, sessionId, 1, parent.Definition, SessionMode.Text, null, SessionStatus.Created,
            [entry], "", 0, null, source.ProfileId, now, now, source.AgentInstanceId,
            Title: string.IsNullOrWhiteSpace(title) ? objective[..Math.Min(objective.Length, 80)] : title.Trim(),
            LastEntrySequence: 1, Purpose: SessionPurpose.OngoingDefault, CompletionPolicy: SessionCompletionPolicy.Default,
            LifecycleSource: LifecycleTransitionSource.System, LifecycleChangedAt: now,
            ModelSelection: new(source.PinnedModel.CatalogKey, source.PinnedModel.ProviderAlias, source.PinnedModel.ModelId,
                ModelSelectionSource.Host, source.PinnedModel.ReasoningEffort), PinnedPersona: source.PinnedPersona,
            PinnedPersonaRevision: parent.PinnedPersonaRevision, Origin: origin, Surfaces: SessionSurface.BackgroundWork);
        return (session, run);
    }

    public static (SessionSnapshot Session, AgentRun Run) ForManual(Guid sessionId, Guid entryId, Guid activationId,
        Guid runId, Guid responseId, Guid profileId, AgentInstance instance, AgentDefinition definition, AgentIdentity persona,
        AgentRunModelPin model, IReadOnlyList<EffectiveSkill> skills, string key, string objective, string title, DateTimeOffset now)
    {
        var entry = new ConversationEntry(entryId, 1, activationId, ConversationRole.User, objective, null, EntryStatus.Completed, SessionMode.Text, 0, objective.Length, now);
        var activation = new Activation(activationId, sessionId, ActivationKind.ManualBackground, [entryId], activationId, null, null, null, key, now);
        var run = AgentRun.Create(runId, new(instance.InstanceId, profileId), new(activation, definition.Id, definition.Version, persona, responseId, AgentRunOutputContract.BackgroundOutcome),
            model, AgentRunLimits.DefaultMaxAttempts, now, skills, skills.Where(skill => skill.Projection == SkillProjection.Always).Select(skill => skill.Key).ToArray());
        var session = new SessionSnapshot(1, sessionId, 1, definition, SessionMode.Text, null, SessionStatus.Created, [entry], "", 0, null,
            profileId, now, now, instance.InstanceId, Title: title, LastEntrySequence: 1, PinnedPersona: persona,
            PinnedPersonaRevision: instance.PersonaRevision, ModelSelection: new(model.CatalogKey, model.ProviderAlias, model.ModelId, ModelSelectionSource.Host, model.ReasoningEffort),
            Origin: new(SessionOriginKind.ManualBackground, initialBackgroundAgentRunId: runId), Surfaces: SessionSurface.BackgroundWork);
        return (session, run);
    }

    public static (SessionSnapshot Session, AgentRun Run) ForOccurrence(Guid sessionId, Guid entryId,
        Guid activationId, Guid agentRunId, Guid responseId, TriggerOccurrence occurrence,
        AgentDefinition definition, AgentIdentity persona, long personaRevision, ExecutionModelPin model,
        IReadOnlyList<EffectiveSkill> skills, DateTimeOffset admittedAtUtc, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var kind = occurrence.SourceKind switch
        {
            TriggerSourceKind.Schedule => ActivationKind.ScheduledWork,
            TriggerSourceKind.ApplicationEvent => ActivationKind.ApplicationEvent,
            TriggerSourceKind.ManualInvocation => ActivationKind.ManualBackground,
            _ => throw new ArgumentException("Occurrence source is not supported.", nameof(occurrence))
        };
        // The admitted evidence already contains the authored instructions and trigger revision.
        // Keep it unchanged instead of silently substituting later Automation edits.
        var text = $"Background task\nSource: {occurrence.SourceKind}\n"
            + $"Scheduled at: {occurrence.ScheduledAtUtc:O}\nObserved at: {occurrence.ObservedAtUtc:O}\n"
            + $"Task and trigger evidence:\n{occurrence.EvidenceJson}";
        var entry = new ConversationEntry(entryId, 1, occurrence.SourceEventId, ConversationRole.User,
            text, null, EntryStatus.Completed, SessionMode.Text, 0, text.Length, admittedAtUtc);
        var activation = new Activation(activationId, sessionId, kind, [entryId], occurrence.SourceEventId,
            occurrence.OccurrenceId, null, null, occurrence.DedupeKey, admittedAtUtc);
        var run = AgentRun.Create(agentRunId,
            new AgentRunOwner(occurrence.Owner.AgentInstanceId, occurrence.Owner.ProfileId),
            new AgentRunAdmission(activation, definition.Id, definition.Version, persona, responseId, AgentRunOutputContract.BackgroundOutcome),
            new AgentRunModelPin(model.CatalogKey, model.ProviderAlias, model.ModelId, model.ReasoningEffort),
            AgentRunLimits.DefaultMaxAttempts, admittedAtUtc, pinnedSkillCatalog: skills,
            activeSkillKeys: skills.Where(skill => skill.Projection == SkillProjection.Always)
                .Select(skill => skill.Key).ToArray());
        var origin = occurrence.AutomationId is { } automationId
            ? new SessionOrigin(SessionOriginKind.AutomationOccurrence, initialBackgroundAgentRunId: agentRunId,
                automationId: automationId, triggerOccurrenceId: occurrence.OccurrenceId,
                originatingSessionId: occurrence.CompletionDelivery.SessionId, reportCompletionToOrigin: occurrence.CompletionDelivery.SessionId is not null)
            : new SessionOrigin(SessionOriginKind.SourceOccurrence, initialBackgroundAgentRunId: agentRunId,
                triggerOccurrenceId: occurrence.OccurrenceId);
        var session = new SessionSnapshot(1, sessionId, 1, definition, SessionMode.Text, null,
            SessionStatus.Created, [entry], string.Empty, 0, null, occurrence.Owner.ProfileId,
            admittedAtUtc, admittedAtUtc, occurrence.Owner.AgentInstanceId,
            Title: string.IsNullOrWhiteSpace(title) ? "Background task" : title.Trim()[..Math.Min(title.Trim().Length, 80)],
            LastEntrySequence: 1, Purpose: SessionPurpose.OngoingDefault,
            CompletionPolicy: SessionCompletionPolicy.Default, LifecycleSource: LifecycleTransitionSource.System,
            LifecycleChangedAt: admittedAtUtc,
            ModelSelection: new SessionModelSelection(model.CatalogKey, model.ProviderAlias, model.ModelId,
                ModelSelectionSource.Host, model.ReasoningEffort), PinnedPersona: persona,
            PinnedPersonaRevision: personaRevision, Origin: origin, Surfaces: origin.InitialSurface);
        return (session, run);
    }
}
