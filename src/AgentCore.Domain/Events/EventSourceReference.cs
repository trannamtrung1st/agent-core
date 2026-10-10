namespace AgentCore.Domain.Events;

public sealed record EventSourceReference
{
    private EventSourceReference(string kind, string? key, Guid? eventId)
    { Kind = kind; Key = key; EventId = eventId; }
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public string Kind { get; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    [System.Text.Json.Serialization.JsonPropertyName("key")]
    public string? Key { get; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    [System.Text.Json.Serialization.JsonPropertyName("eventId")]
    public Guid? EventId { get; }
    public static EventSourceReference Builtin(string key) => CoreEventCatalog.Keys.Contains(key, StringComparer.Ordinal)
        ? new("builtin", key, null) : throw new ArgumentException("Built-in Event is unavailable.");
    public static EventSourceReference Webhook(Guid eventId) => eventId != Guid.Empty
        ? new("webhook", null, eventId) : throw new ArgumentException("Webhook Event identity is required.");
    public static EventSourceReference Parse(string kind, string? key, Guid? eventId) => (kind, key, eventId) switch
    {
        ("builtin", not null, null) => Builtin(key),
        ("webhook", null, not null) => Webhook(eventId.Value),
        _ => throw new ArgumentException("Event source must have exactly one typed locator.")
    };
}
