namespace AgentCore.Application.Tools;

public static class SandboxLimits
{
    public const string Image = "busybox:1.36";
    public const int MemoryBytes = 64 * 1024 * 1024;
    public const int NanoCpus = 500_000_000;
    public const int PidsLimit = 32;
    public const int MaxOutputBytes = 64 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    public const string LabelKey = "agentcore.sandbox";
    public const string SessionLabelKey = "agentcore.session";
}
