using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareArtifactVerification(Guid id, Guid instance, PreparedArtifactVerification value, long instanceRevision, long personaRevision)
    {
        if (id == Guid.Empty || instance == Guid.Empty || value.SessionId == Guid.Empty || value.ArtifactId == Guid.Empty
            || instanceRevision < 1 || personaRevision < 1 || string.IsNullOrWhiteSpace(value.DisplayName) || value.DisplayName.Length > 200
            || string.IsNullOrWhiteSpace(value.ContentType) || value.ContentType.Length > 200 || value.ByteSize < 0
            || value.Sha256Hex is not { Length: 64 } || value.Sha256Hex.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid exact Artifact verification.");
        var json = JsonSerializer.Serialize(new { operation = "artifacts.verify", permission = "artifacts.read", resourceType = "artifact",
            resourceId = value.ArtifactId.ToString("D"), verification = value, expectedInstanceRevision = instanceRevision, expectedPersonaRevision = personaRevision });
        return new(id, instance, null, instanceRevision, personaRevision, json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), ArtifactVerification: value);
    }
}
