using System.Text.Json;
using AgentCore.Domain.Events;

namespace AgentCore.Infrastructure.Persistence;

internal static class EventBucketPacking
{
    private const int MaxSources = 24;
    private const int MaxSourceBytes = 5500;
    private const int FramingBytes = 256;

    internal static bool CanAppend(IReadOnlyList<EventBucketSource> sources, EventBucketSource source) =>
        sources.Count < MaxSources && FramingBytes + sources.Sum(Size) + Size(source) <= MaxSourceBytes;

    // Match the durable JSON representation, including escaped data, owner pins and full causal sets.
    private static int Size(EventBucketSource source) => JsonSerializer.SerializeToUtf8Bytes(source, CoreEventPersistence.Json).Length + 1;
}
