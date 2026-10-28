using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Ports;

public sealed record FormalWorkflowBookmark(string BookmarkId, string NodeId);
public sealed record FormalWorkflowState(bool Finished, bool Cancelled, bool Faulted, string? TerminalNodeId,
    IReadOnlyList<FormalWorkflowBookmark> Bookmarks);

/// <summary>Internal closed graph/bookmark mechanics. Application admission/evidence owners authorize every call.</summary>
public interface IFormalWorkflowRuntime
{
    ValueTask<bool> IsReadyAsync(CancellationToken ct = default);
    ValueTask EnsureCompiledAsync(WorkflowVersion version, CancellationToken ct = default);
    ValueTask<FormalWorkflowState> StartAsync(WorkflowVersion version, Guid executionId, CancellationToken ct = default);
    ValueTask<FormalWorkflowState?> ReadAsync(Guid tenant, Guid executionId, CancellationToken ct = default);
    ValueTask<FormalWorkflowState> ResumeAsync(Guid tenant, Guid executionId, string bookmarkId, string nodeId, string outcome, CancellationToken ct = default);
    ValueTask CancelAsync(Guid tenant, Guid executionId, CancellationToken ct = default);
}
public sealed class FormalWorkflowRuntimeUnavailableException : Exception
{
    public FormalWorkflowRuntimeUnavailableException(Exception inner) : base("Formal workflow persistence is unavailable.", inner) { }
}
