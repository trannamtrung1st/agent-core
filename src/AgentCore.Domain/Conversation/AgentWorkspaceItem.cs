namespace AgentCore.Domain.Conversation;

// Descriptive metadata from the current filesystem. Paths are observations, never
// immutable file identities, CAS tokens or inferred author/provenance records.
public sealed record AgentWorkspaceItem(TenantWorkspaceReference? WorkspaceOwner, string LogicalPath, string ContentType,
    long ByteSize, DateTimeOffset UpdatedAt, bool Directory = false, string Kind = "file",
    string? LinkTarget = null, uint? Mode = null, Guid? ResourceInstanceId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    AgentCore.Domain.Spaces.SpaceResourceOrigin? ResourceSpaceSource = null);

public static class AgentWorkspaceLimits
{
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const long MaxSpaceBytes = 250L * 1024 * 1024;
    public const int MaxPathChars = 4095;
    public const int MaxPageItems = 256;
}
