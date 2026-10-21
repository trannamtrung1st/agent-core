namespace AgentCore.Api.Realtime;

public sealed class AgentCoreOptions
{
    public string Profile { get; set; } = "Synthetic";
    public string AgentDirectory { get; set; } = "agents";
    public int MaxActiveSessions { get; set; } = 10;
    public int MaxEntriesPerSession { get; set; } = 1000;
    public int PendingVoiceTimeoutMs { get; set; } = 30_000;
    public int DetachGracePeriodSeconds { get; set; } = 30;
}
