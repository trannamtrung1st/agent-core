namespace AgentCore.Infrastructure.Persistence;

public sealed class EmailDraftRecord
{
    public string DraftId { get; set; } = "";
    public string AgentInstanceId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string DraftJson { get; set; } = "{}";
    public long CreatedAtUtc { get; set; }
}
public sealed class EmailSendOperationRecord
{
    public string DraftId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public long DraftRevision { get; set; }
    public string ApprovalId { get; set; } = "";
    public string ActionHash { get; set; } = "";
    public string? ResultJson { get; set; }
    public long ClaimedAtUtc { get; set; }
}

public sealed class EmailAccountSelectionRecord
{
    public string RequestId { get; set; } = "";
    public string SelectionJson { get; set; } = "{}";
    public string Status { get; set; } = "pending";
}
