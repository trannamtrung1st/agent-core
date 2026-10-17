using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    private static readonly JsonSerializerOptions SandboxJson = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static string CanonicalSandboxCommand(SandboxCommandRequest command)
    {
        SandboxCommands.Validate(command);
        if (command.TimeoutSeconds is null || !TenantSandboxProfileRules.ValidId(command.ProfileId)) throw new ArgumentException("An exact saved profile and timeout are required.");
        return JsonSerializer.Serialize(command, SandboxJson);
    }
    public static SandboxCommandRequest SandboxCommand(PreparedSandboxCommand value)
    {
        if (value.CommandJson is null || Encoding.UTF8.GetByteCount(value.CommandJson) > 131072) throw new ArgumentException("Invalid Sandbox command bound.");
        var command = JsonSerializer.Deserialize<SandboxCommandRequest>(value.CommandJson, SandboxJson) ?? throw new ArgumentException("Missing Sandbox command.");
        if (CanonicalSandboxCommand(command) != value.CommandJson || command.ProfileId != value.ProfileId) throw new ArgumentException("Sandbox command is not canonical.");
        return command;
    }
    public static PreparedTenantOperation PrepareSandbox(Guid operation, Guid instance, PreparedSandboxCommand value, long instanceRevision, long personaRevision)
    {
        _ = SandboxCommand(value);
        if (operation == Guid.Empty || instance == Guid.Empty || value.SessionId == Guid.Empty || value.ProfileRevision < 1
            || instanceRevision < 1 || personaRevision < 1 || value.ExpectedLeaseRevision < 0 || !Enum.IsDefined(value.HomeAccess)
            || (value.ExpectedGeneration is null) != (value.ExpectedLeaseRevision == 0) || value.ExpectedGeneration == Guid.Empty
            || value.RegistryCredentialId is null && (value.RegistryCredentialRevision is not null || value.RegistrySecretRevision is not null)
            || value.RegistryCredentialId is not null && (value.RegistryCredentialRevision is not > 0 || value.RegistrySecretRevision is null or < 0)
            || value.RegistryCredentialId == Guid.Empty)
            throw new ArgumentException("Invalid exact Sandbox command witnesses.");
        var json = JsonSerializer.Serialize(new { operation = "sandbox.command.execute", permission = "sandbox.execute", resourceType = "agent",
            resourceId = instance.ToString("D"), expectedInstanceRevision = instanceRevision, expectedPersonaRevision = personaRevision, sandboxCommand = value });
        return new(operation, instance, null, instanceRevision, personaRevision, json,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), SandboxCommand: value);
    }
}
