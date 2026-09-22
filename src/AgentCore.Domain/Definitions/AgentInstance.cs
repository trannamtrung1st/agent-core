using System.Security.Cryptography;
using System.Text;

namespace AgentCore.Domain.Definitions;

public enum AgentInstanceLifecycle
{
    Active
}

public sealed record AgentInstance(
    Guid InstanceId,
    string DefinitionId,
    int ActiveVersion,
    AgentIdentity Persona,
    AgentInstanceLifecycle Lifecycle,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Compatibility)
{
    public static Guid CompatibilityFor(string definitionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("agent-core:compatibility:v1:" + definitionId));
        return new Guid(hash.AsSpan(0, 16));
    }
}
