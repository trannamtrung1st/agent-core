using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public sealed record SandboxImageAvailability(string Status, string Message, bool Eligible,
    long? CredentialRevision = null, long? SecretRevision = null);
public sealed record SandboxImageAuthorization(HumanSessionReference Human, long SecurityRevision, long? ExpectedProfileRevision = null,
    Guid? InstanceId = null, long? ExpectedInstanceRevision = null);

/// <summary>Safe current host/source authority. Protected values and SDK auth stay in Infrastructure.</summary>
public interface ISandboxImageAuthority
{
    ValueTask<SandboxProfile> ResolveAsync(SandboxProfile profile, CancellationToken ct = default);
    ValueTask<SandboxProfile> ResolveAsync(SandboxProfile profile, SandboxImageAuthorization proof, CancellationToken ct = default)
        => throw new NotSupportedException("Tenant image preparation requires current human authority.");
    ValueTask<SandboxImageAvailability> InspectAsync(SandboxProfile profile, CancellationToken ct = default, bool requireVerification = true);
}
