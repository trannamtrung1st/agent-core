using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileSessionWorkspace
{
    public async ValueTask<ScratchSnapshot> CaptureScratchAsync(Guid sessionId, AgentDefinition definition, CancellationToken ct = default)
    {
        await EnsureAsync(sessionId, definition, ct);
        return await WithFilesystemAsync(sessionId, token => CaptureScratchLockedAsync(sessionId, token), ct);
    }

    private async ValueTask<ScratchSnapshot> CaptureScratchLockedAsync(Guid sessionId, CancellationToken ct)
    {
        var tree = SnapshotPhysicalTree(sessionId, ct).OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        var entries = new List<WorkspaceTransferEntry> { new("", true, "inode/directory", []) };
        foreach (var node in tree)
        {
            var bytes = node.Directory ? [] : await File.ReadAllBytesAsync(SafePhysical(sessionId, node.Path), ct);
            if (bytes.LongLength != node.ByteSize) throw AgentCoreErrors.Conflict("Scratch changed during capture.");
            entries.Add(new(node.Path["/workspace/working/".Length..], node.Directory, node.Directory ? "inode/directory" : ContentType(node.Path), bytes));
        }
        var transfer = new WorkspaceTransfer(entries);
        ValidateScratchTransfer(sessionId, transfer);
        return new(transfer, ScratchFingerprint(transfer));
    }

    public async ValueTask CommitScratchAsync(Guid sessionId, WorkspaceTransfer transfer, string expectedFingerprint, CancellationToken ct = default,
        Func<CancellationToken, ValueTask>? validateCommit = null)
    {
        // Stage and compare while holding the SAME gate as canonical writes, copies,
        // reads and deletion. A failed validation or stale tree never changes scratch.
        await WithFilesystemAsync(sessionId, async token =>
        {
            ValidateScratchTransfer(sessionId, transfer);
            var before = await CaptureScratchLockedAsync(sessionId, token);
            if (before.Fingerprint != expectedFingerprint) throw AgentCoreErrors.Conflict("Scratch changed while the command ran. Reset the sandbox before another mutation.");
            var root = SessionRoot(sessionId);
            var working = Path.Combine(root, "working");
            var stage = Path.Combine(root, ".sandbox-stage-" + Guid.NewGuid().ToString("N"));
            var backup = Path.Combine(root, ".sandbox-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                foreach (var e in transfer.Entries.Skip(1))
                {
                    token.ThrowIfCancellationRequested();
                    var target = Path.Combine(stage, e.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (e.Directory) Directory.CreateDirectory(target);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await File.WriteAllBytesAsync(target, e.Bytes, token);
                        if (ComputeSha256Hex(await File.ReadAllBytesAsync(target, token)) != ComputeSha256Hex(e.Bytes))
                            throw AgentCoreErrors.Persistence("Scratch staging verification failed.");
                    }
                }
                // Revalidate authority after staging, inside the canonical write gate.
                // No asynchronous work occurs between this fence and the tree swap.
                if (validateCommit is not null) await validateCommit(token);
                token.ThrowIfCancellationRequested();
                ThrowIfDeleted(sessionId);
                Directory.Move(working, backup);
                try { Directory.Move(stage, working); }
                catch { Directory.Move(backup, working); throw; }
                // Commit has succeeded; cancellation cannot turn it into a partial tree.
                try { Directory.Delete(backup, true); }
                catch (IOException) { /* Recovery removes the now-unreferenced backup. */ }
                catch (UnauthorizedAccessException) { /* Canonical commit already succeeded. */ }
                return 0;
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }, ct);
    }

    private void ValidateScratchTransfer(Guid sessionId, WorkspaceTransfer transfer)
    {
        if (transfer.Entries.Count is < 1 or > WorkspaceStructureLimits.MaxEntries
            || transfer.Entries[0] is not { RelativePath: "", Directory: true } || transfer.Entries[0].Bytes.Length != 0)
            throw AgentCoreErrors.Validation("Invalid scratch snapshot root.");
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var exactPaths = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0; var files = 0;
        foreach (var e in transfer.Entries.Skip(1))
        {
            if (e.RelativePath.Length is < 1 or > 480 || e.RelativePath.Contains('\\') || e.RelativePath.Contains('\0')
                || e.RelativePath.Split('/').Length > WorkspaceStructureLimits.MaxDepth
                || e.RelativePath.Any(char.IsControl) || e.RelativePath != e.RelativePath.Normalize(System.Text.NormalizationForm.FormC)
                || e.RelativePath.Split('/').Any(p => p is "" or "." or "..")
                || e.Directory && e.Bytes.Length != 0 || !paths.TryAdd(e.RelativePath, e.Directory))
                throw AgentCoreErrors.Validation("Scratch contains invalid or colliding paths.");
            ValidateStructurePath(sessionId, "/workspace/working/" + e.RelativePath);
            exactPaths.Add(e.RelativePath);
            WorkspaceStructuralPaths.Normalize(sessionId, [new("mkdir", Path: "/workspace/working/" + e.RelativePath)]);
            if (!e.Directory) { files++; bytes = checked(bytes + e.Bytes.LongLength); }
            if (e.Bytes.LongLength > 50L * 1024 * 1024 || bytes > _maxWritableBytes || files > 4096)
                throw AgentCoreErrors.WorkspaceQuotaExceeded();
        }
        foreach (var path in paths.Keys)
        {
            var parent = path;
            while (parent.Contains('/'))
            {
                parent = parent[..parent.LastIndexOf('/')];
                if (!paths.TryGetValue(parent, out var directory) || !directory || !exactPaths.Contains(parent))
                    throw AgentCoreErrors.Validation("Scratch parent directories must be explicit and complete.");
            }
        }
    }

    private static string ScratchFingerprint(WorkspaceTransfer transfer) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        transfer.Entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal).Select(e => new { e.RelativePath, e.Directory, Size = e.Bytes.LongLength, Hash = ComputeSha256Hex(e.Bytes) })))).ToLowerInvariant();

    private static void RecoverSandboxStages(string root)
    {
        if (!Directory.Exists(root)) return;
        var working = Path.Combine(root, "working");
        var backups = Directory.GetDirectories(root, ".sandbox-backup-*");
        if (!Directory.Exists(working) && backups.Length == 1) Directory.Move(backups[0], working);
        else if (!Directory.Exists(working) && backups.Length > 1) throw AgentCoreErrors.Persistence("Scratch commit requires recovery.");
        foreach (var backup in backups.Where(Directory.Exists)) { DenyAllLinks(backup, root); Directory.Delete(backup, true); }
        foreach (var stage in Directory.GetDirectories(root, ".sandbox-stage-*")) { DenyAllLinks(stage, root); Directory.Delete(stage, true); }
    }
}
