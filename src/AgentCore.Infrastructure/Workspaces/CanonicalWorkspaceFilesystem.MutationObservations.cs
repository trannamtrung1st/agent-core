using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using Microsoft.Win32.SafeHandles;

namespace AgentCore.Infrastructure.Workspaces;

internal sealed partial class CanonicalWorkspaceFilesystem
{
    internal async ValueTask<WorkspaceMutationObservation> ObserveMutationAsync(IReadOnlyList<WorkspaceMutationObservationProbe> input,
        long maxFileBytes, long maxBytes, CancellationToken ct = default)
    {
        if (input.Count is < 1 or > WorkspaceMutationObservationLimits.MaxProbes || maxFileBytes < 1 || maxBytes < 1)
            throw AgentCoreErrors.Validation("Use bounded mutation observation probes.");
        var probes = input.ToArray();
        foreach (var probe in probes)
        {
            if (probe is null || probe.FollowLinks && probe.Recursive) throw AgentCoreErrors.Validation("Invalid mutation observation.");
            ProtectRoot(Relative(probe.Path));
        }
        maxBytes = Math.Min(maxBytes, WorkspaceMutationObservationLimits.MaxBytes);
        maxFileBytes = Math.Min(maxFileBytes, AgentWorkspaceLimits.MaxFileBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append("workspace-mutation-source-v1"); var nodes = 0; long bytes = 0; var buffer = new byte[65536];
        foreach (var probe in probes)
        {
            ct.ThrowIfCancellationRequested(); Append(JsonSerializer.Serialize(probe)); Append(Relative(probe.Path));
            SafeFileHandle root;
            try { root = OpenRoot(); }
            catch (AgentCoreException e) when (e.Code == "NotFound") { Missing(); continue; }
            using (root)
            {
                var relative = probe.FollowLinks ? Resolve(root, Relative(probe.Path), allowMissing: true,
                    observedLink: (path, target) => { Append("link-route"); Append(path); Append(target); }) : Relative(probe.Path);
                if (relative != Relative(probe.Path)) Append("resolved:" + relative);
                SafeFileHandle parent;
                try { parent = Parent(root, relative); }
                catch (AgentCoreException e) when (e.Code == "NotFound") { Missing(); continue; }
                using (parent) await NodeHash(root, parent, Leaf(relative), _logicalRoot + "/" + relative, probe.Recursive, 0).ConfigureAwait(false);
            }
        }
        return new(Convert.ToHexStringLower(hash.GetHashAndReset()), nodes, bytes);

        void Append(string value)
        {
            var encoded = Encoding.UTF8.GetBytes(value); Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, encoded.Length); hash.AppendData(length); hash.AppendData(encoded);
        }
        void Missing()
        {
            if (++nodes > WorkspaceMutationObservationLimits.MaxNodes) throw AgentCoreErrors.Validation("Mutation observation exceeds its traversal bound.");
            Append("missing");
        }
        async ValueTask NodeHash(SafeFileHandle scope, SafeFileHandle parent, string name, string logical, bool recursive, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > WorkspaceMutationObservationLimits.MaxDepth || ++nodes > WorkspaceMutationObservationLimits.MaxNodes)
                throw AgentCoreErrors.Validation("Mutation observation exceeds its traversal bound.");
            var before = LStat(parent, name);
            if (before is null) { Append("missing"); return; }
            Append(JsonSerializer.Serialize(before));
            if (before.Kind == LinkKind) Append(ReadLink(parent, name));
            else if (before.Kind == FileKind)
            {
                if (before.Size < 0 || before.Size > maxFileBytes || before.Size > maxBytes - bytes)
                    throw AgentCoreErrors.Validation("Mutation observation exceeds its byte bound.");
                BeforeOpen?.Invoke(logical);
                using var handle = OpenChild(parent, name); var opened = Metadata(Fd(handle));
                if (opened != before) throw new SecurityConflictException("workspace_source_changed");
                VerifyHardLinks(scope, opened, ct);
                using var stream = new FileStream(handle, FileAccess.Read);
                using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long read = 0;
                while (read < before.Size)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, before.Size - read)), ct).ConfigureAwait(false);
                    if (count == 0) throw new SecurityConflictException("workspace_source_changed");
                    content.AppendData(buffer, 0, count); read += count;
                }
                if (Metadata(Fd(handle)) != before) throw new SecurityConflictException("workspace_source_changed");
                bytes = checked(bytes + read); Append(Convert.ToHexStringLower(content.GetHashAndReset()));
            }
            else if (before.Kind == DirectoryKind)
            {
                using var directory = OpenChild(parent, name, directory: true);
                if (Metadata(Fd(directory)) != before) throw new SecurityConflictException("workspace_source_changed");
                var remainingNodes = WorkspaceMutationObservationLimits.MaxNodes - nodes;
                var names = Names(directory, maximum: remainingNodes).Order(StringComparer.Ordinal).ToArray();
                if (!recursive) nodes += names.Length;
                Append(JsonSerializer.Serialize(names)); // Frame the child list so distinct tree shapes cannot share an encoding.
                foreach (var child in names)
                {
                    Append(child);
                    if (recursive) await NodeHash(scope, directory, child, logical + "/" + child, true, depth + 1).ConfigureAwait(false);
                }
                if (!names.SequenceEqual(Names(directory, maximum: remainingNodes).Order(StringComparer.Ordinal))
                    || Metadata(Fd(directory)) != before) throw new SecurityConflictException("workspace_source_changed");
            }
            if (LStat(parent, name) != before) throw new SecurityConflictException("workspace_source_changed");
        }
    }
}
