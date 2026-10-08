using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public static class AutomationDestinationPolicy
{
    public static bool Eligible(SessionSnapshot? session, TriggerOwner owner) => session is not null
        && session.AgentInstanceId == owner.AgentInstanceId && session.ProfileId == owner.ProfileId
        && session.DurablyDeletedAt is null && session.ArchivedAt is null
        && session.Status is not (SessionStatus.Ended or SessionStatus.Ending)
        && !SessionLifecycle.IsTerminal(session.LifecycleStatus)
        && (session.Status != SessionStatus.Paused || SessionPauseSemantics.IsTransportResumable(session.PauseReason));

    public static async ValueTask<SessionSnapshot> RequireAsync(IMemoryStore memory, TriggerOwner owner, Guid id, CancellationToken ct)
    {
        var session = await memory.LoadMetadataAsync(id, ct).ConfigureAwait(false);
        if (!Eligible(session, owner)) throw AgentCoreErrors.Validation("target-unavailable: select an active owned conversation.");
        return session!;
    }

    public static ExecutionModelPin Pin(SessionSnapshot session, IModelCatalog models, string? modelKey = null,
        string? effort = null, bool requiresVision = false, bool requiresTools = false)
    {
        var selection = session.ModelSelection ?? SessionModelBinder.PinDefault(models, session.Definition);
        if (modelKey is not null && modelKey != selection.CatalogKey || effort is not null && effort != selection.ReasoningEffort)
            throw AgentCoreErrors.Validation("target-model-conflict: the conversation's pinned model is authoritative.");
        var pin = new ExecutionModelPin(selection.CatalogKey, selection.ProviderAlias, selection.ModelId,
            selection.ReasoningEffort, ExecutionModelSource.ConversationDefault);
        if (!ExecutionModelPolicy.Matches(models, pin, out var descriptor) || requiresVision && descriptor?.Vision != true || requiresTools && descriptor?.Tools != true)
            throw AgentCoreErrors.Validation("target-model-unavailable: the conversation model cannot execute this task.");
        return pin;
    }
}
