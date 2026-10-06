using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Workspaces;

public static class WorkspaceTransferPlan
{
    public static IReadOnlyList<WorkspacePlannedOperation> Preflight(IReadOnlyList<WorkspaceTreeEntry> existing,
        string destination, WorkspaceTransfer transfer, string root, long maxBytes, long maxFileBytes, int maxFiles, CancellationToken ct)
    {
        if (transfer.Entries.Count is < 1 or > WorkspaceStructureLimits.MaxEntries
            || transfer.Entries[0].RelativePath != "" || transfer.Entries.Any(e => e.Directory && e.Bytes.Length != 0))
            throw AgentCoreErrors.Validation("Invalid bounded workspace transfer.");
        // Use a temporary logical source in the same tree to reuse collision, parent, path and destination quota checks.
        var source = root == "/home" ? "/home/transfer-source" : "/workspace/working/transfer-source";
        while (existing.Any(e => e.Path.Equals(source, StringComparison.OrdinalIgnoreCase) || e.Path.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase))
            || destination.Equals(source, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(destination + "/", StringComparison.OrdinalIgnoreCase)) source += "-x";
        var input = existing.Concat(transfer.Entries.Select(e =>
        {
            if (e.RelativePath.Length != 0 && (e.RelativePath.StartsWith('/') || e.RelativePath.Split('/').Any(p => p is "" or "." or "..")))
                throw AgentCoreErrors.Validation("Transfer contains an invalid relative path.");
            return new WorkspaceTreeEntry(source + (e.RelativePath.Length == 0 ? "" : "/" + e.RelativePath), e.Directory, e.Bytes.LongLength);
        })).ToArray();
        // Preflight normally checks the initial tree, so grant only the temporary source's bounded capacity.
        var transferBytes = transfer.Entries.Sum(e => e.Bytes.LongLength);
        var plan = WorkspaceTreePlanner.Plan(input, [new("move", Source: source, Destination: destination)], root,
            checked(maxBytes + transferBytes), maxFileBytes, ct, maxFiles + transfer.Entries.Count);
        var resultingBytes = existing.Sum(e => e.ByteSize) + transferBytes;
        var parentCount = plan[0].ParentsToCreate.Count;
        if (resultingBytes > maxBytes || existing.Count + transfer.Entries.Count + parentCount > WorkspaceStructureLimits.MaxEntries
            || existing.Count(e => !e.Directory) + transfer.Entries.Count(e => !e.Directory) > maxFiles)
            throw AgentCoreErrors.WorkspaceQuotaExceeded();
        return plan;
    }
}
