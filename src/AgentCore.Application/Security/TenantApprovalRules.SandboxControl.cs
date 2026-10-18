using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareSandboxControl(Guid operationId, Guid instance, PreparedSandboxControl value, long instanceRevision, long personaRevision)
    {
        if (operationId == Guid.Empty || instance == Guid.Empty || instanceRevision < 1 || personaRevision < 1 || !Enum.IsDefined(value.Kind)
            || value.SessionId == Guid.Empty || value.Generation == Guid.Empty || value.ExecutionId == Guid.Empty
            || value.Kind == SandboxControlKind.StopProcess && value.ExecutionId is null
            || !TenantSandboxProfileRules.ValidId(value.ProfileId) || value.ProfileRevision < 1
            || value.ObservedSessionRevision < 1 || value.ObservedLeaseRevision < 1
            || (value.ExecutionId is null) != (value.ObservedExecutionRevision is null) || value.ObservedExecutionRevision < 1
            || value.TargetHash is not { Length: 64 } || value.TargetHash.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("Invalid exact Sandbox control.");
        var json = JsonSerializer.Serialize(new { operation = "sandbox.control", permission = "sandbox.manage", resourceType = "agent",
            resourceId = instance.ToString("D"), expectedInstanceRevision = instanceRevision, expectedPersonaRevision = personaRevision, sandboxControl = value });
        return new(operationId, instance, null, instanceRevision, personaRevision, json,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), SandboxControl: value);
    }
}
