namespace AgentCore.Application.Tools;

public sealed record ToolApprovalGrant(
    Guid ApprovalId,
    string ToolName,
    string ActionHash,
    Guid ResponseId,
    Guid OperationId,
    Guid RuntimeEpoch);
