using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Tests.Shared;

internal static class AgentRunTestFixtures
{
    internal static SessionSnapshot Snapshot(AgentRunOwner owner, AgentDefinition definition, DateTimeOffset now,
        Guid? sessionId = null, Guid? sourceEventId = null) => new(1, sessionId ?? Guid.NewGuid(), 1, definition,
        SessionMode.Text, null, SessionStatus.Created,
        [new(Guid.NewGuid(), 1, sourceEventId ?? Guid.NewGuid(), ConversationRole.User, "Check the record", null,
            EntryStatus.Completed, SessionMode.Text, 0, 16, now)], "", 0, null, owner.ProfileId, now, now,
        owner.AgentInstanceId, PinnedPersona: definition.Identity,
        ModelSelection: new("scripted-alpha", "primary-llm", "scripted-alpha", ModelSelectionSource.SystemDefault, null));

    internal static AgentRun Run(SessionSnapshot snapshot, DateTimeOffset now, IReadOnlyList<EffectiveSkill>? skills = null,
        Guid? runId = null) => AgentRunAdmissionFactory.ForAcceptedUserBatch(Guid.NewGuid(), runId ?? Guid.NewGuid(),
        Guid.NewGuid(), snapshot, snapshot.Entries.Where(e => e.Role == ConversationRole.User).ToArray(), now, skills ?? []);

    internal static async ValueTask<IReadOnlyList<AgentRun>> ListAsync(this IAgentRunStore store, AgentRunOwner owner,
        int limit, CancellationToken ct = default)
    {
        var items = new List<AgentRun>(); Guid? cursor = null;
        while (items.Count < limit)
        {
            var page = await store.ListPageAsync(owner, null, cursor, Math.Min(50, limit - items.Count), ct);
            items.AddRange(page.Items);
            if (!page.HasMore) break;
            cursor = page.NextCursor;
        }
        return items;
    }
}
