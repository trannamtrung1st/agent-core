using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Events;

public sealed record EventCatalogDescriptor(EventSourceReference Source, string Name, string Key, string Description,
    string State, int SchemaVersion, JsonElement Example, JsonElement FieldSchema, WebhookEventCatalogMetadata? Webhook = null);
public sealed record WebhookEventCatalogMetadata(Guid EventId, string DisplayName, string EventKey, string Kind, string Status,
    long Revision, string CreatedAt, string UpdatedAt, int SubscriberCount, int ActiveSubscriberCount, string? LastReceivedAt);

/// <summary>Discovery is global safe metadata. Instance authority and private activity are separate reads.</summary>
public sealed class UnifiedEventCatalog(IExternalEventStore webhooks, ITriggerStore triggers, ILocalUserProfileService profiles)
{
    public static EventCatalogDescriptor Builtin(string key)
    {
        var reference = EventSourceReference.Builtin(key);
        var data = BuiltInEventDefinitions.ExampleData(key, Guid.Parse("00000000-0000-4000-8000-000000000003"));
        var example = CoreEventDispatcher.Project(new(new(Guid.Empty, "sample", new(Guid.Parse("00000000-0000-4000-8000-000000000003"), AgentCore.Domain.Conversation.LocalUserProfile.Id), key,
            DateTimeOffset.UnixEpoch, data), DateTimeOffset.UnixEpoch, false));
        var fields = JsonSerializer.Deserialize<JsonElement>(data).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind.ToString());
        return new(reference, key, key, BuiltInEventDefinitions.Description(key), "Built-in", 1, example,
            JsonSerializer.SerializeToElement(new { schemaVersion = 1, fields, sourceKind = "core", type = "core." + key }));
    }
    public async ValueTask<IReadOnlyList<EventCatalogDescriptor>> ListAsync(CancellationToken ct = default, string? kind = null)
    {
        await profiles.GetLocalProfileAsync(ct);
        if (kind is not (null or "builtin" or "webhook")) throw new ArgumentException("Catalog kind is invalid.");
        var result = kind == "webhook" ? new List<EventCatalogDescriptor>() : CoreEventCatalog.Keys.Select(Builtin).ToList();
        if (kind == "builtin") return result;
        foreach (var e in await webhooks.ListAsync(ct))
        {
            var subscribers = await triggers.ListEventSubscriptionsAsync(e.ResourceId, activeOnly: false, cancellationToken: ct);
            var activity = await webhooks.ReadActivityAsync(e.ResourceId, ct);
            var example = JsonSerializer.SerializeToElement(new { schemaVersion = 1, type = "webhook." + e.EventKey,
                source = new { kind = "webhook", key = e.EventKey }, data = new { status = "example" } });
            result.Add(new(EventSourceReference.Webhook(e.ResourceId), e.DisplayName, e.EventKey, "Authenticated external signal.", e.Status.ToString(), 1,
                example, JsonSerializer.SerializeToElement(new { schemaVersion = 1, data = "Source-defined object (untrusted evidence)" }),
                new(e.ResourceId, e.DisplayName, e.EventKey, "Webhook", e.Status.ToString(), e.Revision, e.CreatedAtUtc.ToString("O"), e.UpdatedAtUtc.ToString("O"),
                    subscribers.Count, subscribers.Count(a => a.Status == AutomationStatus.Active && a.Triggers.Any(t => t.Enabled && t.Source == EventSourceReference.Webhook(e.ResourceId))),
                    activity.Receipts.FirstOrDefault()?.AdmittedAtUtc.ToString("O"))));
        }
        return result;
    }
}
