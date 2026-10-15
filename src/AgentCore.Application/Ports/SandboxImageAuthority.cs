using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public sealed record SandboxImageAvailability(string Status, string Message, bool Eligible,
    long? CredentialRevision = null, long? SecretRevision = null);

/// <summary>Safe current host/source authority. Protected values and SDK auth stay in Infrastructure.</summary>
public interface ISandboxImageAuthority
{
    ValueTask<SandboxProfile> ResolveAsync(SandboxProfile profile, CancellationToken ct = default);
    ValueTask<SandboxImageAvailability> InspectAsync(SandboxProfile profile, CancellationToken ct = default, bool requireVerification = true);
}
