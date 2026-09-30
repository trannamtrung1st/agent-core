namespace AgentCore.Infrastructure.Persistence;

public sealed class ConversationTurnExecutionRecord
{
    public string ExecutionId { get; set; } = "";

    public string SessionId { get; set; } = "";

    public string SourceUserEntryId { get; set; } = "";

    public string SourceEventId { get; set; } = "";

    public string ResponseId { get; set; } = "";

    public string? AgentInstanceId { get; set; }

    public string? ProfileId { get; set; }

    public string DefinitionId { get; set; } = "";

    public int DefinitionVersion { get; set; }

    public string? PinnedPersonaJson { get; set; }

    public string ModelCatalogKey { get; set; } = "";

    public string ModelProviderAlias { get; set; } = "";

    public string ModelId { get; set; } = "";

    public string? ModelReasoningEffort { get; set; }

    public int Status { get; set; }

    public long Revision { get; set; }

    public string? ClaimGeneration { get; set; }

    public long? ClaimedAtUtc { get; set; }

    public long? ClaimLeaseExpiresAtUtc { get; set; }

    public string? AssistantEntryId { get; set; }

    public bool CancellationRequested { get; set; }

    public long? CancellationRequestedAtUtc { get; set; }

    public long AcceptedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }

    public string? PinnedActiveSkillIdsJson { get; set; }
}
