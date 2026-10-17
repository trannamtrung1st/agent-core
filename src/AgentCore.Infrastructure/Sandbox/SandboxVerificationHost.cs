using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Sandbox;

public sealed class SandboxVerificationHost(OpenSandboxOptions options) : ISandboxVerificationHost
{
    public string Scope => Fingerprint(options);
    internal static string Fingerprint(OpenSandboxOptions value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\0', value.CanonicalWorkspaceRoot, value.WorkspaceDockerHostRoot, value.Endpoint, SandboxProfileVerification.CurrentContractVersion.ToString()))));
}
