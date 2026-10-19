using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Workspaces;

internal sealed partial class CanonicalWorkspaceFilesystem
{
    internal sealed record ArtifactSource(byte[] Bytes, long ByteSize, string Sha256Hex, string ObservationHash);
    private sealed record SourceLink(string Path, string Target, Node Node);
    // Directory timestamps/link counts change when unrelated siblings are created; identity and access metadata do not.
    private sealed record SourceDirectory(ulong Device, ulong Inode, uint Mode, uint Uid, uint Gid)
    {
        internal static SourceDirectory Of(Node node) => new(node.Device, node.Inode, node.Mode, node.Uid, node.Gid);
    }
    internal async ValueTask<ArtifactSource> CaptureArtifactSourceAsync(string path, long maxBytes, bool includeBytes, CancellationToken ct = default)
    {
        if (maxBytes < 0 || maxBytes > int.MaxValue) throw AgentCoreErrors.Validation("Invalid source bound.");
        ct.ThrowIfCancellationRequested();
        using var root = OpenRoot();
        var rootIdentity = SourceDirectory.Of(Metadata(Fd(root)));
        SourceLink ObserveLink(string p, string target)
        {
            using var linkParent = Parent(root, Relative(p)); var linkName = Leaf(Relative(p));
            var node = LStat(linkParent, linkName);
            if (node?.Kind != LinkKind || ReadLink(linkParent, linkName) != target) throw new SecurityConflictException("workspace_source_changed");
            return new(p, target, node);
        }
        var links = new List<SourceLink>();
        var relative = Resolve(root, Relative(path), observedLink: (p, target) => links.Add(ObserveLink(p, target)));
        if (relative.Length == 0) throw AgentCoreErrors.Validation("Artifact source requires an ordinary file.");
        using var parent = Parent(root, relative); var name = Leaf(relative);
        var parentIdentity = SourceDirectory.Of(Metadata(Fd(parent)));
        var before = LStat(parent, name) ?? throw AgentCoreErrors.NotFound("Workspace path was not found.");
        BeforeOpen?.Invoke(path);
        using var handle = OpenChild(parent, name); var opened = Metadata(Fd(handle));
        if (opened != before) throw new SecurityConflictException("workspace_source_changed");
        if (opened.Kind != FileKind) throw AgentCoreErrors.Validation("Artifact source requires an ordinary file.");
        VerifyHardLinks(root, opened, ct);
        if (opened.Size < 0 || opened.Size > maxBytes) throw AgentCoreErrors.WorkspaceQuotaExceeded();
        var bytes = includeBytes ? new byte[checked((int)opened.Size)] : [];
        var scratch = includeBytes ? bytes : new byte[65536];
        using var stream = new FileStream(handle, FileAccess.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long consumed = 0;
        while (consumed < opened.Size)
        {
            var offset = includeBytes ? checked((int)consumed) : 0;
            var length = (int)Math.Min(opened.Size - consumed, scratch.Length - offset);
            var count = await stream.ReadAsync(scratch.AsMemory(offset, length), ct).ConfigureAwait(false);
            if (count == 0) throw new SecurityConflictException("workspace_source_changed");
            hash.AppendData(scratch, offset, count); consumed += count;
        }
        ct.ThrowIfCancellationRequested();
        if (Metadata(Fd(handle)) != opened || LStat(parent, name) != opened) throw new SecurityConflictException("workspace_source_changed");
        var currentLinks = new List<SourceLink>();
        var currentRelative = Resolve(root, Relative(path), observedLink: (p, target) => currentLinks.Add(ObserveLink(p, target)));
        using var currentParent = Parent(root, currentRelative);
        using var currentRoot = OpenRoot();
        if (SourceDirectory.Of(Metadata(Fd(currentRoot))) != rootIdentity || SourceDirectory.Of(Metadata(Fd(currentParent))) != parentIdentity
            || currentRelative != relative || !currentLinks.SequenceEqual(links) || LStat(currentParent, Leaf(currentRelative)) != opened)
            throw new SecurityConflictException("workspace_source_changed");
        var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        var witness = JsonSerializer.Serialize(new { path, relative, root = rootIdentity, parent = parentIdentity, links, node = opened, sha256 = sha });
        ct.ThrowIfCancellationRequested();
        return new(bytes, consumed, sha, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(witness))));
    }
}
