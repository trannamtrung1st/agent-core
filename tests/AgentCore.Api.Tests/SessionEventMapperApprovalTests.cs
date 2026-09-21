using AgentCore.Application.Events;
using AgentCore.Application.Tools;
using AgentCore.Api.Realtime;

namespace AgentCore.Api.Tests;

public sealed class SessionEventMapperApprovalTests
{
    [Fact]
    public void Approval_requested_maps_to_wire_event()
    {
        var context = new EventContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            null);
        var responseId = Guid.NewGuid();
        var mapped = SessionEventMapper.Map(
            new SessionOutput(
                context,
                responseId,
                new ApprovalRequestedOutput(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    ToolCatalog.DemoSensitiveAction,
                    "sensitiveWrite",
                    "Run sensitive demo action: test",
                    new Dictionary<string, string> { ["label"] = "test" },
                    DateTimeOffset.UtcNow.AddMinutes(5))),
            Guid.NewGuid(),
            12);

        Assert.Equal("agent.approval.requested", mapped.Type);
        Assert.Equal(responseId.ToString(), mapped.ResponseId);
        Assert.Equal(ToolCatalog.DemoSensitiveAction, mapped.Payload["toolName"]);
        Assert.Equal("sensitiveWrite", mapped.Payload["effect"]);
    }
}
