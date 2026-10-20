using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareAccountRead(Guid operation, Guid instance, PreparedAccountRead read, long instanceRevision, long personaRevision)
    {
        var args = TenantAccountReadContract.Parse(read.ToolName, read.ArgumentsJson);
        TenantAccountOperationReadRules.Validate(read);
        ValidateAccountWitness(operation, instance, read.Credential, read.Purpose, instanceRevision, personaRevision);
        if (read.SessionId == Guid.Empty || read.Purpose != args.Purpose || read.ArgumentsJson != args.Json || read.Credential.Reference != args.Reference)
            throw new ArgumentException("Invalid exact bound-account read.");
        var json = JsonSerializer.Serialize(new { operation = "accounts.read", permission = "credentials.use", resourceType = "credential", resourceId = read.Credential.CredentialId.ToString("D"),
            expectedInstanceRevision = instanceRevision, expectedPersonaRevision = personaRevision, read });
        return new(operation, instance, null, instanceRevision, personaRevision, json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), AccountRead: read);
    }
}
