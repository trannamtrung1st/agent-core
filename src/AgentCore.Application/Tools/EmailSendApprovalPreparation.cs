namespace AgentCore.Application.Tools;

public sealed record EmailSendApprovalPreparation(
    string ActionHash,
    string Summary,
    Dictionary<string, string> Details);

public sealed record EmailSendApprovalPrepareResult(
    EmailSendApprovalPreparation? Preparation,
    string? ErrorJson);
