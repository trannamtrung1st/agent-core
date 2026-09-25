using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Persistence;

public sealed class FileDefinitionResourceContentStore(string root) : IDefinitionResourceContentStore
{
    public async ValueTask StoreVerifiedAsync(string contentSha256, byte[] content, CancellationToken cancellationToken = default)
    {
        DefinitionResourceStoredContent.EnsureStorePayload(contentSha256, content);
        AttachmentBlobKeys.EnsureSafeRoot(root);
        Directory.CreateDirectory(root);
        var path = ResolvePath(contentSha256);
        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            DefinitionResourceContentHasher.EnsureMatches(contentSha256, existing);
            DefinitionResourceStoredContent.EnsureIdempotentMatch(existing, content);
            return;
        }

        var temp = path + ".partial";
        await File.WriteAllBytesAsync(temp, content, cancellationToken).ConfigureAwait(false);
        DefinitionResourceContentHasher.EnsureMatches(contentSha256, await File.ReadAllBytesAsync(temp, cancellationToken).ConfigureAwait(false));
        File.Move(temp, path, overwrite: false);
    }

    public async ValueTask<byte[]?> ReadAsync(string contentSha256, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(contentSha256);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        DefinitionResourceContentHasher.EnsureMatches(contentSha256, bytes);
        return bytes;
    }

    private string ResolvePath(string contentSha256)
    {
        if (contentSha256.Length != 64 || contentSha256.Any(static ch => !Uri.IsHexDigit(ch)))
        {
            throw AgentCoreErrors.Validation("contentSha256 must be a lowercase SHA-256 hex digest.");
        }

        var path = Path.GetFullPath(Path.Combine(root, contentSha256));
        var rootFull = Path.GetFullPath(root);
        if (!path.StartsWith(rootFull, StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Validation("contentSha256 is not permitted.");
        }

        return path;
    }
}
