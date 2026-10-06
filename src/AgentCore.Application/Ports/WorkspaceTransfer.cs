namespace AgentCore.Application.Ports;

// Bounded exact-byte snapshot. Paths are relative to the copied node, never physical or owner-selecting.
public sealed record WorkspaceTransferEntry(string RelativePath, bool Directory, string ContentType, byte[] Bytes);
public sealed record WorkspaceTransfer(IReadOnlyList<WorkspaceTransferEntry> Entries);
