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
            ? ToolRegistry.All.Select(d => d.Name).Where(n => candidate.Environment.WorkspacePolicy.Semantics == WorkspaceSemantics.AgentWorkspaceV2
                ? n is not (ToolCatalog.WorkspaceRetain or ToolCatalog.WorkspaceCheckout) : n != ToolCatalog.WorkspaceCwd).Order(StringComparer.Ordinal).ToArray()
            : authority.ResolvedCapabilities.Order(StringComparer.Ordinal).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', names)))).ToLowerInvariant();
        return candidate with { Environment = candidate.Environment with { Capabilities = authority with { ResolvedCapabilities = names, AuthorizationFingerprint = fingerprint } } };
    }
}
