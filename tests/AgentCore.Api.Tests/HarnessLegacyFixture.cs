using AgentCore.Application.Admin;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

// Pure normalization of historical disposable test candidates; no Harness executor or approval owner.
internal static class HarnessLegacyFixture
{
    // Explicit owner repair of a disposable draft; immutable built-ins keep their historical authority.
    internal static AgentDefinitionCandidate WithoutRetiredSandboxTool(AgentDefinitionCandidate candidate)
    {
        var environment = candidate.Environment!;
        return candidate with { Environment = environment with {
            ToolAllowlist = environment.ToolAllowlist?.Where(name => name != "sandbox.run").ToArray(),
            Capabilities = environment.Capabilities is { } authority ? authority with {
                ResolvedCapabilities = authority.ResolvedCapabilities.Where(name => name != "sandbox.run").ToArray(),
                AuthorizationFingerprint = null } : null,
            Projection = environment.Projection is { } projection ? projection with {
                AlwaysCapabilities = projection.AlwaysCapabilities.Where(name => name != "sandbox.run").ToArray() } : null
        } };
    }
}
