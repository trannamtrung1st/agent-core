using System.Security.Cryptography;
using System.Net.Http.Headers;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public static class TenantArtifactRules
{
    public static void Query(Guid tenant, Guid session, int limit)
    { if (tenant == Guid.Empty || session == Guid.Empty || limit is < 1 or > 100) throw new ArgumentException("Use bounded owned Artifact queries."); }
    public static ArtifactRecord Record(TenantRuntimeFrame frame, Guid artifact, string name, string type, ReadOnlySpan<byte> bytes, DateTimeOffset at)
    {
        TenantRuntimeRules.RequireOwner(frame.Owner);
        if (artifact == Guid.Empty || frame.SessionId == Guid.Empty || frame.RunId == Guid.Empty || string.IsNullOrWhiteSpace(type) || type is not { Length: > 0 and <= 200 }
            || type.Any(char.IsControl) || !MediaTypeHeaderValue.TryParse(type, out _) || bytes.Length > ArtifactLimits.MaxBytesEach) throw new ArgumentException("Invalid owned Artifact publication.");
        return new(artifact, frame.SessionId, WorkspaceFileNames.Sanitize(name), type.Trim(), bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), null, null, DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()), frame.RunId, frame.Owner.TenantId);
    }
    public static AccessScope Scope(Guid tenant, Guid artifact) => AccessScope.ForResource(tenant, "artifact", artifact.ToString("D"));
    public static bool TryId(AccessScope scope, out Guid artifact)
    {
        artifact = default;
        return scope.Kind == AccessScopeKind.Resource && scope.TenantId is { } tenant && tenant != Guid.Empty && scope.ResourceType == "artifact"
            && Guid.TryParseExact(scope.ResourceId, "D", out artifact) && artifact != Guid.Empty && scope.ResourceId == artifact.ToString("D");
    }
    public static string BlobKey(Guid tenant, Guid session, Guid artifact) => $"tenants/{tenant:N}/{session:N}/{artifact:N}";
    public static bool SamePublication(ArtifactRecord saved, ArtifactRecord next) => (saved with { CreatedAt = next.CreatedAt }) == next;
    public static void RequireContent(ArtifactRecord record, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != record.ByteSize || Convert.ToHexStringLower(SHA256.HashData(bytes)) != record.Sha256Hex)
            throw new AccessDeniedException("artifact_content_unavailable");
    }
}
