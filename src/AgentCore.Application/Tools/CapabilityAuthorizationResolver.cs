using System.Security.Cryptography;
using System.Text;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class CapabilityAuthorizationResolver
{
    public static AgentDefinitionCandidate ResolveCandidate(AgentDefinitionCandidate candidate)
    {
        if (candidate.Environment?.Capabilities is not { } authority
            || authority.ResolvedCapabilities is null) return candidate;
        var names = authority.Mode == "All"
            ? ToolRegistry.DefinitionAuthorizable.Select(d => d.Name).Order(StringComparer.Ordinal).ToArray()
            : authority.ResolvedCapabilities.Where(n => !ToolRegistry.TryGet(n, out var descriptor) || descriptor.DefinitionAuthorizable).Order(StringComparer.Ordinal).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', names)))).ToLowerInvariant();
        return candidate with
        {
            Environment = candidate.Environment with
            {
                Capabilities = authority with { ResolvedCapabilities = names, AuthorizationFingerprint = fingerprint },
                // Bootstrap is projected by Core. Other invalid context projections remain
                // visible to validation rather than being silently accepted.
                Projection = candidate.Environment.Projection is { } projection
                    ? projection with { AlwaysCapabilities = projection.AlwaysCapabilities.Where(n => n != ToolCatalog.CapabilitiesLoad).ToArray() }
                    : null
            }
        };
    }
}
