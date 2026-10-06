namespace AgentCore.Infrastructure.Persistence;

public sealed class AgentWorkspaceRow
{
    public string ItemId { get; set; } = "";
    public string AgentInstanceId { get; set; } = "";
    public string PathKey { get; set; } = "";
    public long Revision { get; set; }
    public long ByteSize { get; set; }
    public string BlobKey { get; set; } = "";
    public string MetadataJson { get; set; } = "";
}
