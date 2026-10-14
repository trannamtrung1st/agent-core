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
        Func<CancellationToken, ValueTask>? validateCommit = null, Guid? commitId = null)
    {
        // Stage and compare while holding the SAME gate as canonical writes, copies,
        // reads and deletion. A failed validation or stale tree never changes scratch.
        await WithFilesystemAsync(sessionId, async token =>
        {
            RecoverSandboxStages(SessionRoot(sessionId));
            ValidateScratchTransfer(sessionId, transfer);
            var before = await CaptureScratchLockedAsync(sessionId, token);
            if (before.Fingerprint != expectedFingerprint) throw AgentCoreErrors.Conflict("Scratch changed while the command ran. Reset the sandbox before another mutation.");
            var root = SessionRoot(sessionId);
            var working = Path.Combine(root, "working");
            var id = commitId ?? Guid.CreateVersion7();
            var journal = CommitJournalPath(root, id);
            if (File.Exists(journal)) throw AgentCoreErrors.Conflict("Scratch commit already has durable evidence. Reconcile; never repeat the import.");
            var stage = Path.Combine(root, ".sandbox-stage-" + id.ToString("N"));
            var backup = Path.Combine(root, ".sandbox-backup-" + id.ToString("N"));
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
                if (BeforeScratchCommitValidation is not null) await BeforeScratchCommitValidation(token);
                if (validateCommit is not null) await validateCommit(token);
                token.ThrowIfCancellationRequested();
                ThrowIfDeleted(sessionId);
                WriteCommitEvidence(root, id, "prepared");
                ScratchCommitFault?.Invoke("beforeSwap");
                Directory.Move(working, backup);
                ScratchCommitFault?.Invoke("afterBackup");
                try { Directory.Move(stage, working); }
                catch { Directory.Move(backup, working); throw; }
                ScratchCommitFault?.Invoke("afterSwap");
                WriteCommitEvidence(root, id, "committed");
                ScratchCommitFault?.Invoke("afterEvidence");
                // Commit has succeeded; cancellation cannot turn it into a partial tree.
                try { Directory.Delete(backup, true); }
                catch (IOException) { /* Recovery removes the now-unreferenced backup. */ }
                catch (UnauthorizedAccessException) { /* Canonical commit already succeeded. */ }
                return 0;
            }
            finally
            {
                // A prepared journal owns its stage/backup until recovery resolves
                // the swap. Deleting either here would destroy crash evidence.
                if (!File.Exists(journal) && Directory.Exists(stage)) Directory.Delete(stage, true);
            }
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

    // Test-only synchronous crash points; no async gap is added after the authority fence.
    internal Func<CancellationToken, ValueTask>? BeforeScratchCommitValidation { get; set; }
    internal Action<string>? ScratchCommitFault { get; set; }
    private static string CommitJournalPath(string root, Guid id) => Path.Combine(root, ".sandbox-commit-" + id.ToString("N") + ".json");

    private static void WriteCommitEvidence(string root, Guid id, string state)
    {
        var path = CommitJournalPath(root, id);
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, state);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static string ReadCommitEvidence(string root, Guid id)
    {
        var path = CommitJournalPath(root, id);
        if (!File.Exists(path)) return "notCommitted";
        var state = JsonSerializer.Deserialize<string>(File.ReadAllText(path));
        if (state is "committed" or "notCommitted") return state;
        if (state != "prepared") return "unknown";
        var stage = Directory.Exists(Path.Combine(root, ".sandbox-stage-" + id.ToString("N")));
        var backup = Directory.Exists(Path.Combine(root, ".sandbox-backup-" + id.ToString("N")));
        var working = Directory.Exists(Path.Combine(root, "working"));
        if (backup && !stage && working) return "committed";
        if (stage && (working && !backup || !working && backup)) return "notCommitted";
        return "unknown";
    }

    public ValueTask<string> GetScratchCommitStatusAsync(Guid sessionId, Guid commitId, CancellationToken ct = default) =>
        WithFilesystemAsync(sessionId, _ =>
        {
            var root = SessionRoot(sessionId);
            DenyEscapingLinks(root, _root);
            return ValueTask.FromResult(ReadCommitEvidence(root, commitId));
        }, ct);

    private static void RecoverSandboxStages(string root)
    {
        if (!Directory.Exists(root)) return;
        var working = Path.Combine(root, "working");
        foreach (var journal in Directory.GetFiles(root, ".sandbox-commit-*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileName(journal)[16..^5], "N", out var id))
                throw AgentCoreErrors.Persistence("Scratch commit evidence is invalid.");
            var status = ReadCommitEvidence(root, id);
            if (status == "unknown") throw AgentCoreErrors.Persistence("Scratch commit requires recovery; its outcome is uncertain.");
            var backup = Path.Combine(root, ".sandbox-backup-" + id.ToString("N"));
            var stage = Path.Combine(root, ".sandbox-stage-" + id.ToString("N"));
            if (!Directory.Exists(working) && Directory.Exists(backup)) Directory.Move(backup, working);
            WriteCommitEvidence(root, id, status);
            foreach (var tree in new[] { backup, stage }.Where(Directory.Exists)) { DenyAllLinks(tree, root); Directory.Delete(tree, true); }
        }
        // Compatibility recovery for pre-journal stages, and staging failures
        // before a prepared record was flushed. No command is ever replayed.
        var backups = Directory.GetDirectories(root, ".sandbox-backup-*");
        if (!Directory.Exists(working) && backups.Length == 1) Directory.Move(backups[0], working);
        else if (!Directory.Exists(working) && backups.Length > 1) throw AgentCoreErrors.Persistence("Scratch commit requires recovery.");
        foreach (var backup in backups.Where(Directory.Exists)) { DenyAllLinks(backup, root); Directory.Delete(backup, true); }
        foreach (var stage in Directory.GetDirectories(root, ".sandbox-stage-*")) { DenyAllLinks(stage, root); Directory.Delete(stage, true); }
    }
}
