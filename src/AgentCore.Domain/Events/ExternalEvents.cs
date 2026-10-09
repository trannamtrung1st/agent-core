namespace AgentCore.Domain.Events;

public static class EventKeys
{
    public static bool IsValid(string? key) => key is { Length: > 0 and <= 64 }
        && key[0] is >= 'a' and <= 'z'
        && key.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_')
        && key[^1] is not '.' and not '-' and not '_';
    public static string Require(string key) => IsValid(key) ? key
        : throw new ArgumentException("Event key must be 1–64 lowercase letters, digits, dots, hyphens or underscores, starting with a letter and ending with a letter or digit.");
}

public enum WebhookEventKind
{
    Webhook = 0
}

public enum WebhookEventStatus
{
    Active = 0,
    Revoked = 1
}

public sealed class WebhookEvent
{
    public WebhookEvent(
        Guid resourceId,
        string displayName,
        WebhookEventKind kind,
        string eventKey,
        string? credentialHash,
        WebhookEventStatus status,
        long revision,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (resourceId == Guid.Empty)
        {
            throw new ArgumentException("Event identifier is required.", nameof(resourceId));
        }

        if (!EventKeys.IsValid(eventKey))
        {
            throw new ArgumentException("Event key is required.", nameof(eventKey));
        }

        if (!Enum.IsDefined(kind) || !Enum.IsDefined(status))
        {
            throw new ArgumentException("Event kind or status is not valid.");
        }

        if (revision < 1)
        {
            throw new ArgumentException("Event revision starts at 1.");
        }

        DisplayName = RequireName(displayName);
        if (createdAtUtc.Offset != TimeSpan.Zero || updatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Event timestamps must be UTC.");
        }

        if (status == WebhookEventStatus.Active && string.IsNullOrWhiteSpace(credentialHash))
        {
            throw new ArgumentException("An active event requires a credential hash.");
        }

        ResourceId = resourceId;
        Kind = kind;
        EventKey = eventKey;
        CredentialHash = string.IsNullOrWhiteSpace(credentialHash) ? null : credentialHash.Trim();
        Status = status;
        Revision = revision;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid ResourceId { get; }

    public string DisplayName { get; }

    public WebhookEventKind Kind { get; }

    public string EventKey { get; }

    public string? CredentialHash { get; }

    public WebhookEventStatus Status { get; }

    public long Revision { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    private static string RequireName(string? displayName)
    {
        var name = displayName?.Trim() ?? "";
        if (name.Length is < 1 or > 80)
        {
            throw new ArgumentException("Event name must be 1-80 characters.");
        }

        foreach (var character in name)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException("Event name must not contain control characters.");
            }
        }

        return name;
    }
}

public enum ExternalEventDeliveryStatus
{
    Pending = 0,
    Admitted = 1,
    Skipped = 2,
    Filtered = 3,
    FilterError = 4,
    Coalesced = 5
}

public sealed record ExternalEventTarget(Guid AutomationId, Guid AgentInstanceId, Guid ProfileId, EventSubscriptionSnapshot? Snapshot = null);

public sealed record ExternalEventDelivery(
    Guid EventId,
    Guid AutomationId,
    Guid AgentInstanceId,
    Guid ProfileId,
    ExternalEventDeliveryStatus Status, EventSubscriptionSnapshot? Snapshot = null, EventFilterResult? Decision = null);

public sealed class ExternalEvent
{
    public ExternalEvent(
        Guid eventId,
        Guid resourceId,
        string sourceEventId,
                DateTimeOffset occurredAtUtc,
        DateTimeOffset admittedAtUtc,
        string evidenceJson)
    {
        if (eventId == Guid.Empty || resourceId == Guid.Empty)
        {
            throw new ArgumentException("External event identifiers are required.");
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
        ResourceId = resourceId;
        SourceEventId = sourceEventId.Trim();
        OccurredAtUtc = occurredAtUtc;
        AdmittedAtUtc = admittedAtUtc;
        EvidenceJson = evidenceJson;
    }

    public Guid EventId { get; }

    public Guid ResourceId { get; }

    public string SourceEventId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public DateTimeOffset AdmittedAtUtc { get; }

    public string EvidenceJson { get; }
}
