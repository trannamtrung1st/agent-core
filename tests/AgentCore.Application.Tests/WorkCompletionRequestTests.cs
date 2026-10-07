using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Work;

namespace AgentCore.Application.Tests;

public sealed class WorkCompletionRequestTests
{
    [Theory]
    [InlineData("{\"ok\":true,\"data\":{\"error\":\"saved field\"}}", false)]
    [InlineData("{\"error\":null,\"ok\":true}", false)]
    [InlineData("{\"error\":\"\",\"ok\":true}", false)]
    [InlineData("Action completed", false)]
    [InlineData("{\"error\":\"forbidden\",\"message\":\"Denied by policy\"}", true)]
    public void NoAction_distinguishes_rejected_actions_from_successful_action_data(string payload, bool accepted)
    {
        using var args = JsonDocument.Parse("""{"outcome":"NoAction","summary":"Nothing to do","attentionRequired":false}""");
        Assert.Equal(accepted, WorkCompletionRequest.TryParse(args.RootElement,
            [new(ModelRole.Tool, payload, ToolCallId: "write", Name: ToolCatalog.WorkspaceWrite)], out _, out _, out _));
    }
}
