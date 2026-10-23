using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Api.Realtime;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Tests;

public sealed class SessionEventMapperApprovalTests
{
    [Fact]
    public void Durable_approval_notification_carries_only_saved_request_and_run_references()
    {
        var context = new EventContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), null);
        var requestId = Guid.NewGuid(); var runId = Guid.NewGuid(); var responseId = Guid.NewGuid();
        var mapped = SessionEventMapper.Map(new(context, responseId, new DurableApprovalPendingOutput(requestId, runId)), Guid.NewGuid(), 12);
        Assert.Equal("agent.approval.pending", mapped.Type);
        Assert.Equal(responseId.ToString(), mapped.ResponseId);
        Assert.Equal(requestId.ToString("D"), mapped.Payload["requestId"]);
        Assert.Equal(runId.ToString("D"), mapped.Payload["runId"]);
        Assert.Equal(2, mapped.Payload.Count);
    }

    [Fact]
    public void Session_ready_has_no_response_bound_approval_or_modal_action_payload()
    {
        var context = new EventContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), null);
        var responseId = Guid.NewGuid();
        var ready = new SessionReadyProjection(SessionMode.Text, null, SessionStatus.Attached,
            new PublicAgentDescriptor("examiner", 1, "Examiner", "role", "desc", false), null, null,
            new RecognitionCapabilities(false, false, false, false), new SynthesisCapabilities(false, false, false, false, false, []),
            "none", 1, [], responseId, "browser", "browser");
        var mapped = SessionEventMapper.Map(new(context, null, new ReadyOutput(ready)), Guid.NewGuid(), 3);
        Assert.Equal("session.ready", mapped.Type);
        Assert.Equal(responseId.ToString(), mapped.Payload["activeResponseId"]);
        Assert.DoesNotContain("pendingApproval", mapped.Payload.Keys);
        Assert.Null(typeof(SessionReadyProjection).GetProperty("PendingApproval"));
        Assert.Null(typeof(OutputPayload).Assembly.GetType("AgentCore.Application.Events.PublicPendingApproval"));
    }
}
