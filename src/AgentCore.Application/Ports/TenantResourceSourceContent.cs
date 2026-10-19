namespace AgentCore.Application.Ports;

// Byte-store facet only. The caller must first resolve current tenant/Instance ownership and IAM.
// Implementations refuse missing, corrupt, unsafe or over-bound content without creating a root.
public interface ITenantResourceSourceContentStore
{
    ValueTask<byte[]?> ReadVerifiedResourceSourceAsync(string contentSha256, long expectedByteSize, CancellationToken ct = default);
}
