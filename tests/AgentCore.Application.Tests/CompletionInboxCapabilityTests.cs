using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class CompletionInboxCapabilityTests
{
    [Theory]
    [InlineData("background.list")]
    [InlineData("background.inspect")]
    [InlineData("background.take")]
    [InlineData("background.acknowledge")]
    [InlineData("execution.wait")]
    public void Result_handoff_and_wait_have_explicit_authorizable_capabilities(string name)
    {
        Assert.True(ToolRegistry.TryGet(name, out var descriptor));
        Assert.True(descriptor.DefinitionAuthorizable);
    }
}
