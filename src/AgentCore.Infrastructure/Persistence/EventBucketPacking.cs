using System.Text.Json;
using AgentCore.Domain.Events;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Persistence;

internal static class EventBucketPacking
{
    private const int MaxSources = AgentCore.Domain.Triggers.TriggerLimits.MaxEventBucketSources;
    private const int MaxSourceBytes = 5500;
    private const int FramingBytes = 256;

    internal static bool Contains(CoreEventBucket bucket, DateTimeOffset receivedAt) =>
        bucket.Subscription.Dispatch.WindowSeconds is { } window && window > 0
        && receivedAt >= bucket.DueAtUtc.AddSeconds(-window) && receivedAt < bucket.DueAtUtc;

    internal static bool CanAppend(IReadOnlyList<EventBucketSource> sources, EventBucketSource source) =>
        sources.Count < MaxSources && FramingBytes + sources.Sum(Size) + Size(source) <= MaxSourceBytes;

    // Match the durable JSON representation, including escaped data, owner pins and full causal sets.
    private static int Size(EventBucketSource source) => JsonSerializer.SerializeToUtf8Bytes(source, CoreEventPersistence.Json).Length + 1;
}
