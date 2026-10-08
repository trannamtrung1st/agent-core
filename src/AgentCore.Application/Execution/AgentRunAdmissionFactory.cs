using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Execution;

/// <summary>Freezes one effective model turn; the caller commits its input graph through IAgentRunStore.</summary>
public static class AgentRunAdmissionFactory
{
    public static AgentRun ForAcceptedUserBatch(Guid activationId, Guid agentRunId, Guid responseId,
        SessionSnapshot snapshot, IReadOnlyList<ConversationEntry> users, DateTimeOffset admittedAtUtc,
        IReadOnlyList<EffectiveSkill> catalog)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(catalog);
        if (users.Count is < 1 or > Activation.MaxSourceEntries
            || users.Any(entry => entry.Role != ConversationRole.User || entry.Status != EntryStatus.Completed)
            || users.Select(entry => entry.EntryId).Distinct().Count() != users.Count
            || users.Zip(users.Skip(1)).Any(pair => pair.First.Sequence >= pair.Second.Sequence))
            throw AgentCoreErrors.Validation("A turn requires an ordered, bounded batch of accepted user entries.");
        foreach (var user in users)
        {
            if (snapshot.Entries.SingleOrDefault(entry => entry.EntryId == user.EntryId) != user)
                throw AgentCoreErrors.Validation("User inputs must belong to the admitted Session snapshot.");
        }
        var instanceId = snapshot.AgentInstanceId;
        if (snapshot.ProfileId is not { } profileId || profileId == Guid.Empty)
            throw AgentCoreErrors.Validation("AgentRun admission requires an Agent Instance and trusted profile.");
        var model = snapshot.ModelSelection
            ?? throw AgentCoreErrors.Validation("AgentRun admission requires a resolved model.");
        var persona = snapshot.PinnedPersona
            ?? throw AgentCoreErrors.Validation("AgentRun admission requires a pinned persona.");
        // Stable across crash/replay even if the proposal receives fresh execution IDs.
        var batchKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join(',', users.Select(entry => entry.EntryId.ToString("D")))))).ToLowerInvariant();
        var activation = new Activation(activationId, snapshot.SessionId, ActivationKind.UserTurn,
            users.Select(entry => entry.EntryId).ToArray(), users[0].SourceEventId ?? users[0].EntryId,
            null, null, null, $"user-batch:{batchKey}", admittedAtUtc);
        return AgentRun.Create(agentRunId, new AgentRunOwner(instanceId, profileId),
            new AgentRunAdmission(activation, snapshot.Definition.Id, snapshot.Definition.Version, persona, responseId),
            new AgentRunModelPin(model.CatalogKey, model.ProviderAlias, model.ModelId, model.ReasoningEffort),
            AgentRunLimits.DefaultMaxAttempts, admittedAtUtc,
            pinnedSkillCatalog: catalog,
            activeSkillKeys: catalog.Where(skill => skill.Projection == SkillProjection.Always).Select(skill => skill.Key).ToArray());
    }
}
