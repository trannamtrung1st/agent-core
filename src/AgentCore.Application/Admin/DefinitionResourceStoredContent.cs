using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Admin;

public static class DefinitionResourceStoredContent
{
    public static async ValueTask<byte[]> RequireForBindingAsync(
        IDefinitionResourceContentStore content,
        string contentSha256,
        long declaredByteLength,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        var bytes = await content.ReadAsync(contentSha256, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            throw AgentCoreErrors.Validation("Resource content was not found for the declared hash.");
        }

        if (bytes.LongLength != declaredByteLength)
        {
            throw AgentCoreErrors.Validation("byteLength does not match stored resource content.");
        }

        DefinitionResourceContentHasher.EnsureMatches(contentSha256, bytes);
        DefinitionResourcePolicies.ValidateContentSize(bytes.LongLength);
        var normalizedMediaType = DefinitionResourcePolicies.NormalizeMediaType(mediaType);
        DefinitionResourcePolicies.RejectSecretsInTextualContent(normalizedMediaType, bytes);
        return bytes;
    }

    public static void EnsureStorePayload(string contentSha256, ReadOnlySpan<byte> content)
    {
        DefinitionResourceContentHasher.EnsureMatches(contentSha256, content);
        DefinitionResourcePolicies.ValidateContentSize(content.Length);
    }

    public static void EnsureIdempotentMatch(ReadOnlySpan<byte> existing, ReadOnlySpan<byte> incoming)
    {
        if (!existing.SequenceEqual(incoming))
        {
            throw AgentCoreErrors.Conflict("Resource content hash is already bound to different bytes.");
        }
    }
}
