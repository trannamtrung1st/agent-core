namespace AgentCore.Infrastructure.Persistence;

public sealed class EmailWatchRecord
{
    public string AccountId { get; set; } = "";
    public bool Enabled { get; set; }
    public long NextCheckAtUtc { get; set; }
    public long RuntimeRevision { get; set; }
    public string StateJson { get; set; } = "";
}
// Hidden authority receipt; never enters Event data or model evidence.
public sealed class EmailWatchEventRecord
{
    public string EventId { get; set; } = "";
    public string AuthorityJson { get; set; } = "";
}
