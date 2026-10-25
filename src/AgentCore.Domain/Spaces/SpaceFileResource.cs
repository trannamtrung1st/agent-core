using System.Text.Json.Serialization;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Spaces;

/// <summary>Immutable byte provenance belongs to a Space; the execution Instance is not its source owner.</summary>
public sealed record SpaceResourceOrigin(Guid TenantId, Guid SpaceId, Guid ResourceId, long Revision)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || SpaceId == Guid.Empty || ResourceId == Guid.Empty || Revision < 1)
            throw new ArgumentException("Invalid Space resource provenance.");
    }
    [JsonIgnore] public string Key => SpaceResourceKeys.Create(SpaceId, ResourceId);
}

public static class SpaceResourceKeys
{
    public const string VirtualRoot = "/space/resources";
    public static string Create(Guid space, Guid resource)
    {
        if (space == Guid.Empty || resource == Guid.Empty) throw new ArgumentException("A stable Space resource identity is required.");
        return $"space:{space:D}:{resource:D}";
    }
    public static bool TryParse(string? key, out Guid space, out Guid resource)
    {
        space = resource = Guid.Empty;
        return key is { Length: 79 } && key.StartsWith("space:", StringComparison.Ordinal) && key[42] == ':'
            && Guid.TryParseExact(key.AsSpan(6, 36), "D", out space) && space != Guid.Empty
            && Guid.TryParseExact(key.AsSpan(43, 36), "D", out resource) && resource != Guid.Empty
            && key == Create(space, resource);
    }
    public static string VirtualPath(Guid space, Guid resource, string logicalPath) => $"{VirtualRoot}/{space:D}/{resource:D}/{logicalPath}";
}

/// <summary>A Space-scoped immutable content binding. SourcePath is an observation, never an identity or grant.</summary>
public sealed record SpaceFileResource(Guid TenantId, Guid SpaceId, Guid ResourceId, string LogicalPath,
    AgentDefinitionResourceKind Kind, string MediaType, string ContentSha256, long ByteLength, string SourcePath,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt = null)
{
    [JsonIgnore] public SpaceResourceOrigin Origin => new(TenantId, SpaceId, ResourceId, Revision);
    [JsonIgnore] public string Key => SpaceResourceKeys.Create(SpaceId, ResourceId);
    public void Validate()
    {
        Origin.Validate();
        if (Kind is not (AgentDefinitionResourceKind.Knowledge or AgentDefinitionResourceKind.Reference or AgentDefinitionResourceKind.Template or AgentDefinitionResourceKind.StaticAsset)
            || string.IsNullOrWhiteSpace(LogicalPath) || LogicalPath.Length > 240 || LogicalPath != LogicalPath.Trim() || LogicalPath.StartsWith('/')
            || LogicalPath.Any(c => char.IsControl(c) || c is '\\' or ':') || LogicalPath.Split('/').Any(p => p is "" or "." or "..")
            || string.IsNullOrWhiteSpace(MediaType) || MediaType.Length > 256 || MediaType.Any(char.IsControl)
            || ContentSha256 is not { Length: 64 } || ContentSha256.Any(c => !char.IsAsciiHexDigit(c)) || ContentSha256 != ContentSha256.ToLowerInvariant()
            || ByteLength is < 1 or > AgentResourceLimits.MaxItemBytes || UpdatedAt < CreatedAt || DeletedAt is { } deleted && deleted < CreatedAt
            || SourcePath is null || !SourcePath.StartsWith("/home/", StringComparison.Ordinal) || SourcePath.Length > 4095 || SourcePath.Any(c => char.IsControl(c) || c == '\\')
            || SourcePath[6..].Split('/').Any(p => p is "" or "." or "..")) throw new ArgumentException("Invalid Space file resource.");
    }
    public EffectiveAgentResource Effective()
    {
        Validate();
        if (DeletedAt is not null) throw new ArgumentException("A deleted Space resource is unavailable.");
        return new(Key, LogicalPath, Kind, MediaType, ContentSha256, ByteLength, SpaceResourceKeys.VirtualPath(SpaceId, ResourceId, LogicalPath), Origin);
    }
}
