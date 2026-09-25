using System.Collections.Concurrent;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Definitions;

public sealed class InMemoryDefinitionResourceContentStore : IDefinitionResourceContentStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    public ValueTask StoreVerifiedAsync(string contentSha256, byte[] content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DefinitionResourceStoredContent.EnsureStorePayload(contentSha256, content);
        var copy = content.ToArray();
        if (_blobs.TryGetValue(contentSha256, out var existing))
        {
            DefinitionResourceStoredContent.EnsureIdempotentMatch(existing, copy);
            return ValueTask.CompletedTask;
        }

        _blobs[contentSha256] = copy;
        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]?> ReadAsync(string contentSha256, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_blobs.TryGetValue(contentSha256, out var content))
        {
            return ValueTask.FromResult<byte[]?>(null);
        }

        return ValueTask.FromResult<byte[]?>(content.ToArray());
    }
}
