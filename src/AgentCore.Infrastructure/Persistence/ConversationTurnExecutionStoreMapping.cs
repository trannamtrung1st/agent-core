using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;

namespace AgentCore.Infrastructure.Persistence;

internal static class ConversationTurnExecutionStoreMapping
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ConversationTurnExecutionRecord ToRecord(ConversationTurnExecution item)
    {
        var row = new ConversationTurnExecutionRecord();
        Apply(row, item);
        return row;
    }

    public static void Apply(ConversationTurnExecutionRecord row, ConversationTurnExecution item)
    {
        row.ExecutionId = Id(item.ExecutionId);
        row.SessionId = Id(item.SessionId);
        row.SourceUserEntryId = Id(item.SourceUserEntryId);
        row.SourceEventId = Id(item.SourceEventId);
        row.ResponseId = Id(item.ResponseId);
        row.AgentInstanceId = OptionalId(item.AgentInstanceId);
        row.ProfileId = OptionalId(item.ProfileId);
        row.DefinitionId = item.DefinitionId;
        row.DefinitionVersion = item.DefinitionVersion;
        row.PinnedPersonaJson = item.PinnedPersona is null
            ? null
            : JsonSerializer.Serialize(item.PinnedPersona, Json);
        row.ModelCatalogKey = item.PinnedModel.CatalogKey;
        row.ModelProviderAlias = item.PinnedModel.ProviderAlias;
        row.ModelId = item.PinnedModel.ModelId;
        row.ModelReasoningEffort = item.PinnedModel.ReasoningEffort;
        row.Status = (int)item.Status;
        row.Revision = item.Revision;
        row.ClaimGeneration = item.Claim is null ? null : Id(item.Claim.Generation);
        row.ClaimedAtUtc = item.Claim is null ? null : item.Claim.ClaimedAtUtc.ToUnixTimeMilliseconds();
        row.ClaimLeaseExpiresAtUtc = item.Claim is null ? null : item.Claim.LeaseExpiresAtUtc.ToUnixTimeMilliseconds();
        row.AssistantEntryId = OptionalId(item.AssistantEntryId);
        row.CancellationRequested = item.CancellationRequested;
        row.CancellationRequestedAtUtc = Unix(item.CancellationRequestedAtUtc);
        row.AcceptedAtUtc = item.AcceptedAtUtc.ToUnixTimeMilliseconds();
        row.UpdatedAtUtc = item.UpdatedAtUtc.ToUnixTimeMilliseconds();
        row.PinnedActiveSkillIdsJson = item.PinnedActiveSkillIds.Count == 0
            ? null
            : JsonSerializer.Serialize(item.PinnedActiveSkillIds, Json);
        row.SkillLoadCount = item.SkillLoadCount;
    }

    public static ConversationTurnExecution ToDomain(ConversationTurnExecutionRecord row)
    {
        AgentIdentity? persona = null;
        if (!string.IsNullOrWhiteSpace(row.PinnedPersonaJson))
        {
            persona = JsonSerializer.Deserialize<AgentIdentity>(row.PinnedPersonaJson, Json);
        }

        WorkClaim? claim = null;
        if (row.ClaimGeneration is not null
            && row.ClaimedAtUtc is long claimed
            && row.ClaimLeaseExpiresAtUtc is long lease)
        {
            claim = new WorkClaim(
                Guid.Parse(row.ClaimGeneration),
                DateTimeOffset.FromUnixTimeMilliseconds(claimed),
                DateTimeOffset.FromUnixTimeMilliseconds(lease));
        }

        IReadOnlyList<string> pinnedSkills = [];
        if (!string.IsNullOrWhiteSpace(row.PinnedActiveSkillIdsJson))
        {
            pinnedSkills = JsonSerializer.Deserialize<string[]>(row.PinnedActiveSkillIdsJson, Json) ?? [];
        }

        return ConversationTurnExecution.Restore(
            Guid.Parse(row.ExecutionId),
            Guid.Parse(row.SessionId),
            Guid.Parse(row.SourceUserEntryId),
            Guid.Parse(row.SourceEventId),
            Guid.Parse(row.ResponseId),
            ParseOptional(row.AgentInstanceId),
            ParseOptional(row.ProfileId),
            row.DefinitionId,
            row.DefinitionVersion,
            persona,
            new WorkModelPin(row.ModelCatalogKey, row.ModelProviderAlias, row.ModelId, row.ModelReasoningEffort),
            (ConversationTurnExecutionStatus)row.Status,
            row.Revision,
            claim,
            ParseOptional(row.AssistantEntryId),
            row.CancellationRequested,
            row.CancellationRequestedAtUtc is long cancelled
                ? DateTimeOffset.FromUnixTimeMilliseconds(cancelled)
                : null,
            DateTimeOffset.FromUnixTimeMilliseconds(row.AcceptedAtUtc),
            DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc),
            pinnedSkills,
            row.SkillLoadCount);
    }

    public static Exception Map(Exception exception) =>
        exception switch
        {
            WorkItemTransitionException transition => transition.Failure switch
            {
                WorkTransitionFailure.StaleRevision => AgentCoreErrors.Conflict(transition.Message),
                WorkTransitionFailure.StaleGeneration => AgentCoreErrors.Conflict(transition.Message),
                WorkTransitionFailure.Terminal => AgentCoreErrors.Validation(transition.Message),
                WorkTransitionFailure.Illegal => AgentCoreErrors.Validation(transition.Message),
                WorkTransitionFailure.NotClaimable => AgentCoreErrors.Conflict(transition.Message),
                _ => exception
            },
            _ => exception
        };

    private static string Id(Guid value) => value.ToString("D");

    private static string? OptionalId(Guid? value) => value is null ? null : Id(value.Value);

    private static Guid? ParseOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Guid.Parse(value);

    private static long? Unix(DateTimeOffset? value) => value?.ToUnixTimeMilliseconds();
}
