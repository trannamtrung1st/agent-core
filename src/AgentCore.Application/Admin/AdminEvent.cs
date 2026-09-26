namespace AgentCore.Application.Admin;

public enum AdminEventActorKind
{
    LocalOwner,
    System
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
    TriggerRegistrationRevoked,
    InstanceArchived,
    InstanceUnarchived
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
