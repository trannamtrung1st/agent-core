using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Api.Realtime;
using AgentCore.Domain.Conversation;

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

    [Fact]
    public void Session_ready_replays_pending_approval_when_present()
    {
        var context = new EventContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            null);
        var responseId = Guid.NewGuid();
        var approvalId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var ready = new SessionReadyProjection(
            SessionMode.Text,
            null,
            SessionStatus.Attached,
            new PublicAgentDescriptor("examiner", 1, "Examiner", "role", "desc", false),
            null,
            null,
            new RecognitionCapabilities(false, false, false, false),
            new SynthesisCapabilities(false, false, false, false, false, []),
            "none",
            1,
            [],
            responseId,
            "browser",
            "browser",
            PendingApproval: new PublicPendingApproval(
                approvalId,
                responseId,
                operationId,
                ToolCatalog.DemoSensitiveAction,
                "sensitiveWrite",
                "Approve demo action",
                new Dictionary<string, string> { ["label"] = "test" },
                expiresAt));
        var mapped = SessionEventMapper.Map(
            new SessionOutput(context, null, new ReadyOutput(ready)),
            Guid.NewGuid(),
            3);

        Assert.Equal("session.ready", mapped.Type);
        var pending = Assert.IsAssignableFrom<IDictionary<string, object?>>(mapped.Payload["pendingApproval"]);
        Assert.Equal(approvalId.ToString(), pending["approvalId"]);
        Assert.Equal(responseId.ToString(), pending["responseId"]);
        Assert.Equal(operationId.ToString(), pending["operationId"]);
        Assert.Equal(ToolCatalog.DemoSensitiveAction, pending["toolName"]);
    }
}
