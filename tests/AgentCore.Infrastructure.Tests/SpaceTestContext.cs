using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Spaces;

namespace AgentCore.Infrastructure.Tests;

// Pure filesystem tests declare a stable Space witness; executable tenant workflows
// use the capture admitted by their real fixture stores instead.
internal sealed class SpaceTestContext
{
    internal RunSpaceWitness Witness { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, 1, 1, null, 0, 1);
    internal SandboxWorkspaceAccess Access(HomeAccess home, HomeAccess privateAccess = HomeAccess.Disabled, bool workingReadOnly = false) =>
        new(Witness, home, privateAccess, workingReadOnly);
}
