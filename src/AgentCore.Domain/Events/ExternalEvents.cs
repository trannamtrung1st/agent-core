namespace AgentCore.Domain.Events;

public static class ExternalEventTypes
{
    public const string OrderPlaced = "order.placed";

    public static bool IsAllowed(string? eventType) =>
        string.Equals(eventType, OrderPlaced, StringComparison.Ordinal);
}

public enum ExternalEventSourceKind
{
    Webhook = 0
}

public enum ExternalEventSourceStatus
{
    Active = 0,
    Revoked = 1
}

public sealed class ExternalEventSource
{
    public ExternalEventSource(
        Guid sourceId,
        string displayName,
        ExternalEventSourceKind kind,
        Guid sourceKey,
        string? credentialHash,
        ExternalEventSourceStatus status,
        long revision,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("Event source identifier is required.", nameof(sourceId));
        }

        if (sourceKey == Guid.Empty)
        {
            throw new ArgumentException("Event source key is required.", nameof(sourceKey));
        }

        if (!Enum.IsDefined(kind) || !Enum.IsDefined(status))
        {
            throw new ArgumentException("Event source kind or status is not valid.");
        }

        if (revision < 1)
        {
            throw new ArgumentException("Event source revision starts at 1.");
        }

        DisplayName = RequireName(displayName);
        if (createdAtUtc.Offset != TimeSpan.Zero || updatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Event source timestamps must be UTC.");
        }

        if (status == ExternalEventSourceStatus.Active && string.IsNullOrWhiteSpace(credentialHash))
        {
            throw new ArgumentException("An active event source requires a credential hash.");
        }

        SourceId = sourceId;
        Kind = kind;
        SourceKey = sourceKey;
        CredentialHash = string.IsNullOrWhiteSpace(credentialHash) ? null : credentialHash.Trim();
        Status = status;
        Revision = revision;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid SourceId { get; }

    public string DisplayName { get; }

    public ExternalEventSourceKind Kind { get; }

    public Guid SourceKey { get; }

    public string? CredentialHash { get; }

    public ExternalEventSourceStatus Status { get; }

    public long Revision { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    private static string RequireName(string? displayName)
    {
        var name = displayName?.Trim() ?? "";
        if (name.Length is < 1 or > 80)
        {
            throw new ArgumentException("Event source name must be 1-80 characters.");
        }

        foreach (var character in name)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException("Event source name must not contain control characters.");
            }
        }

        return name;
    }
}

public enum ExternalEventDeliveryStatus
{
    Pending = 0,
    Admitted = 1,
    Skipped = 2
}

public sealed record ExternalEventTarget(Guid AutomationId, Guid AgentInstanceId, Guid ProfileId);

public sealed record ExternalEventDelivery(
    Guid EventId,
    Guid AutomationId,
    Guid AgentInstanceId,
    Guid ProfileId,
    ExternalEventDeliveryStatus Status);

public sealed class ExternalEvent
{
    public ExternalEvent(
        Guid eventId,
        Guid sourceId,
        string sourceEventId,
        string eventType,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset admittedAtUtc,
        string evidenceJson)
    {
        if (eventId == Guid.Empty || sourceId == Guid.Empty)
        {
            throw new ArgumentException("External event identifiers are required.");
        }

        if (!ExternalEventTypes.IsAllowed(eventType))
        {
            throw new ArgumentException("Event type is not allowed.", nameof(eventType));
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero || admittedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("External event timestamps must be UTC.");
        }

        if (string.IsNullOrWhiteSpace(evidenceJson) || evidenceJson.Length > 4096)
        {
            throw new ArgumentException("External event evidence is not valid.");
        }

        EventId = eventId;
        SourceId = sourceId;
        SourceEventId = sourceEventId.Trim();
        EventType = eventType;
        OccurredAtUtc = occurredAtUtc;
        AdmittedAtUtc = admittedAtUtc;
        EvidenceJson = evidenceJson;
    }

    public Guid EventId { get; }

    public Guid SourceId { get; }

    public string SourceEventId { get; }

    public string EventType { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public DateTimeOffset AdmittedAtUtc { get; }

    public string EvidenceJson { get; }
}
