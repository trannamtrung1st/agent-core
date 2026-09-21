namespace AgentCore.Domain.Conversation;

public static class WorkspaceLimits
{
    public const long MaxWritableBytes = 250L * 1024 * 1024;
    public const int MaxListEntries = 256;
    public const int MaxPatchEdits = 32;
}
