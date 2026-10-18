using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareArtifact(Guid operationId, Guid instance, PreparedArtifactPublication artifact, long instanceRevision, long personaRevision)
    {
        var args = TenantArtifactToolContract.Parse(artifact.ArgumentsJson);
        if (operationId == Guid.Empty || instance == Guid.Empty || artifact.SessionId == Guid.Empty || instanceRevision < 1 || personaRevision < 1
            || args.CanonicalArguments != artifact.ArgumentsJson) throw new ArgumentException("Invalid exact Artifact publication.");
        var json = JsonSerializer.Serialize(new { operation = "artifacts.publish", permission = "artifacts.create", resourceType = "session",
            resourceId = artifact.SessionId.ToString("D"), artifact, expectedInstanceRevision = instanceRevision, expectedPersonaRevision = personaRevision });
        return new(operationId, instance, null, instanceRevision, personaRevision, json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), ArtifactPublication: artifact);
    }
}
