using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantRunRules
{
    public static AccessScope Scope(Guid tenant, Guid run) => AccessScope.ForResource(tenant, "agent-run", run.ToString("D"));
    public static bool TryId(AccessScope scope, out Guid id)
    {
        id = Guid.Empty;
        return scope.Kind == AccessScopeKind.Resource && scope.TenantId is { } tenant && tenant != Guid.Empty && scope.ResourceType == "agent-run"
            && Guid.TryParseExact(scope.ResourceId, "D", out id) && id != Guid.Empty && scope.ResourceId == id.ToString("D");
    }
    public static SessionSnapshot Admit(TenantSession session, TenantHumanRunAdmission admission, TenantAgent agent, TenantSessionInitiator initiator, bool live = false)
    {
        var run = admission.Run; var a = run.Admission.Activation; var context = run.Admission.ExecutionContext;
        var current = session.Snapshot; var input = admission.Input;
        TenantAttachmentBindingRules.Shape(admission);
        TenantInputInterruptionRules.Shape(admission);
        if (context is null || context.Origin != ExecutionOrigin.UserMessage || context.OriginalInitiatorPrincipalId != initiator.PrincipalId
            || context.TenantId != session.Owner.TenantId || context.AgentPrincipalId != session.Owner.ExecutorPrincipalId
            || run.Owner != AgentRunOwner.ForTenant(session.Owner.TenantId, session.Owner.AgentInstanceId, session.Owner.ExecutorPrincipalId)
            || agent.PrincipalId != context.AgentPrincipalId || agent.TenantId != context.TenantId || agent.Instance.InstanceId != run.AgentInstanceId
            || current.ProfileId is not null || current.SessionId != run.SessionId || !run.IsInitialQueued
            || a.Kind != ActivationKind.UserTurn || a.SourceEntryIds.Count != 1 || a.SourceEntryIds[0] != input.EntryId
            || input.Role != ConversationRole.User || input.Status != EntryStatus.Completed || input.ResponseId is not null
            || (live ? input.Sequence <= current.DurableLastEntrySequence : input.Sequence != current.DurableLastEntrySequence + 1) || input.SourceEventId is null
            || a.SourceEventId != input.SourceEventId || a.DedupeKey != $"human:{initiator.PrincipalId:D}:{input.SourceEventId:D}"
            || input.Text.Length > 8000 || string.IsNullOrWhiteSpace(input.Text) && input.Attachments is not { Count: > 0 }
            || input.Parts is not null || input.Envelope is not null || input.Failure is not null
            || run.Admission.Configuration?.InstanceRevision != agent.Instance.Revision || run.DefinitionId != agent.Instance.DefinitionId
            || run.DefinitionVersion != agent.Instance.ActiveVersion || run.PinnedPersona != agent.Instance.Persona
            || run.Admission.Configuration.PersonaRevision != agent.Instance.PersonaRevision)
            throw new ArgumentException("Invalid tenant human Run admission.");
        if (current.Revision != admission.ExpectedSessionRevision || agent.Instance.Revision != admission.ExpectedInstanceRevision)
            throw new SecurityConflictException("stale_revision");
        if (!current.Surfaces.HasFlag(SessionSurface.ChatList) || current.DurablyDeletedAt is not null || current.ArchivedAt is not null || LifecycleTransition.IsTerminal(current))
            throw AgentCoreErrors.Validation("Session is unavailable for new input.");
        if (!live && (current.Status is SessionStatus.Attached or SessionStatus.Ending || current.PendingAgentInputIds.Count != 0))
            throw new SecurityConflictException("session_in_use");
        if (current.LifecycleStatus != SessionLifecycleStatus.Active) throw AgentCoreErrors.Validation("Resume the Session before sending input.");
        return current with { Revision = checked(current.Revision + 1), Entries = live ? [..current.Entries, input] : [input], LastEntrySequence = input.Sequence,
            LastUserActivityAt = live && current.LastUserActivityAt > input.CreatedAt ? current.LastUserActivityAt : input.CreatedAt,
            UpdatedAt = live && current.UpdatedAt > input.CreatedAt ? current.UpdatedAt : input.CreatedAt };
    }
    public static bool SameMessage(AgentRun saved, ConversationEntry savedInput, TenantHumanRunAdmission proposed) =>
        saved.Owner == proposed.Run.Owner && saved.SessionId == proposed.Run.SessionId
        && saved.Admission.ExecutionContext?.Origin == ExecutionOrigin.UserMessage
        && saved.Admission.ExecutionContext?.OriginalInitiatorPrincipalId == proposed.Run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId
        && savedInput.SourceEventId == proposed.Input.SourceEventId && savedInput.Text == proposed.Input.Text
        && saved.Admission.Activation.EvidenceJson == proposed.Run.Admission.Activation.EvidenceJson
        && (savedInput.Attachments ?? []).SequenceEqual(proposed.Input.Attachments ?? []);
}
