using System.Collections.Concurrent;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Definitions;

public sealed class InMemoryDefinitionResourceContentStore : IDefinitionResourceContentStore, ITenantResourceSourceContentStore
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
    public async ValueTask<byte[]?> ReadVerifiedResourceSourceAsync(string contentSha256, long expectedByteSize, CancellationToken ct = default)
    {
        AgentCore.Application.Admin.DefinitionResourcePolicies.ValidateContentSize(expectedByteSize);
        if (contentSha256.Length != 64 || contentSha256.Any(c => !char.IsAsciiHexDigit(c)) || contentSha256 != contentSha256.ToLowerInvariant())
            throw AgentCore.Application.Sessions.AgentCoreErrors.Validation("Use a canonical content hash.");
        var bytes = await ReadAsync(contentSha256, ct).ConfigureAwait(false);
        if (bytes is not null) AgentCore.Application.Admin.AgentInstanceResourceService.Verify(contentSha256, expectedByteSize, bytes);
        return bytes;
    }
}
