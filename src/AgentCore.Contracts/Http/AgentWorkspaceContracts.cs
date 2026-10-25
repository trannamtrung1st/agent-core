namespace AgentCore.Contracts.Http;

public sealed record WorkspaceOwnerResponse(string Kind, Guid TenantId, Guid? SpaceId = null, Guid? SessionId = null);
public sealed record SpaceResourceOriginResponse(Guid TenantId, Guid SpaceId, Guid ResourceId, long Revision);
public sealed record AgentWorkspaceItemResponse(WorkspaceOwnerResponse? WorkspaceOwner, string LogicalPath, string ContentType,
    long ByteSize, DateTimeOffset UpdatedAt, bool Directory = false, string Kind = "file", string? LinkTarget = null, uint? Mode = null, Guid? ResourceInstanceId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SpaceResourceOriginResponse? ResourceSpaceSource = null);
public sealed record AgentWorkspacePageResponse(IReadOnlyList<AgentWorkspaceItemResponse> Items, long? UsedBytes, long? TotalItems,
    string? NextPath, long MaxFileBytes, long MaxSpaceBytes, string UsageEnforcement, bool Truncated);
