namespace AgentCore.Application.Admin;

public enum AdminEventActorKind
{
    LocalOwner,
    System,
    Agent
}

public enum AdminEventOperationKind
{
    DraftCreated,
    DraftDeleted,
    PublicationCreated,
    PublicationDeprecated,
    ManagedInstanceCreated,
    InstanceDefinitionVersionChanged,
    PersonaChanged,
    MemoryItemDeleted,
    MemoryScopeReset,
    AutomationRevoked,
    InstanceArchived,
    InstanceUnarchived,
    InstanceDeleted,
    DefinitionDeleted,
    HarnessPolicyChanged,
    HarnessPreparationChanged,
    ExperienceChanged,
    AutomationChanged,
    InstanceSkillsChanged,
    BrowserPrivacyChanged
}

public sealed record AdminEvent(
    Guid EventId,
    Guid OperationId,
    DateTimeOffset OccurredAt,
    AdminEventActorKind ActorKind,
    AdminEventOperationKind Operation,
    string TargetType,
    string TargetId,
    long? Revision,
    int? Version,
    string SummaryJson);

public sealed record AdminEventAppend(
    Guid OperationId,
    DateTimeOffset OccurredAt,
    AdminEventActorKind ActorKind,
    AdminEventOperationKind Operation,
    string TargetType,
    string TargetId,
    long? Revision,
    int? Version,
    string SummaryJson);

public sealed record AdminEventListQuery(
    string? TargetType = null,
    string? TargetId = null,
    int Limit = 100);
