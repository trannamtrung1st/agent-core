namespace AgentCore.Infrastructure.Persistence;

public sealed class StructuredMemoryRecord
{
    public string MemoryId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public int Kind { get; set; }
    public int Status { get; set; }
    public string Subject { get; set; } = "";
    public string Content { get; set; } = "";
    public string SubjectKey { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceEntryIdsJson { get; set; } = "[]";
    public string? SupersedesMemoryId { get; set; }
    public long ProvenanceRecordedAtUtc { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}
