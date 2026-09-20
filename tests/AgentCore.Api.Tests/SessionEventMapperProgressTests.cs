using AgentCore.Api.Realtime;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using MessagePack;

namespace AgentCore.Api.Tests;

public sealed class SessionEventMapperProgressTests
{
    [Theory]
    [InlineData(ResponseProgressKind.Preparing, "preparing")]
    [InlineData(ResponseProgressKind.ReadingAttachments, "readingAttachments")]
    [InlineData(ResponseProgressKind.RunningTool, "runningTool")]
    [InlineData(ResponseProgressKind.WaitingExternal, "waitingExternal")]
    [InlineData(ResponseProgressKind.Finalizing, "finalizing")]
    public void Progress_kind_maps_to_specified_camelCase_wire_value(
        ResponseProgressKind kind,
        string expected)
    {
        var mapped = MapProgress(kind, ResponseProgressState.Started);
        Assert.Equal("agent.progress", mapped.Type);
        Assert.Equal(expected, mapped.Payload["kind"]);
        Assert.Equal(1, mapped.ProtocolVersion);
    }

    [Theory]
    [InlineData(ResponseProgressState.Started, "started")]
    [InlineData(ResponseProgressState.Updated, "updated")]
    [InlineData(ResponseProgressState.Completed, "completed")]
    [InlineData(ResponseProgressState.Failed, "failed")]
    public void Progress_state_maps_to_specified_camelCase_wire_value(
        ResponseProgressState state,
        string expected)
    {
        var mapped = MapProgress(ResponseProgressKind.RunningTool, state, Guid.NewGuid());
        Assert.Equal(expected, mapped.Payload["state"]);
    }

    [Fact]
    public void Progress_uses_outer_response_id_and_optional_operation_id()
    {
        var responseId = Guid.Parse("019944af-0000-7000-8000-000000000012");
        var operationId = Guid.Parse("019944af-0000-7000-8000-000000000099");
        var mapped = MapProgress(
            ResponseProgressKind.RunningTool,
            ResponseProgressState.Started,
            operationId,
            "Running tools…",
            responseId);

        Assert.Equal("agent.progress", mapped.Type);
        Assert.Equal(responseId.ToString(), mapped.ResponseId);
        Assert.False(mapped.Payload.ContainsKey("responseId"));
        Assert.Equal(operationId.ToString(), mapped.Payload["operationId"]);
        Assert.Equal("runningTool", mapped.Payload["kind"]);
        Assert.Equal("started", mapped.Payload["state"]);
        Assert.Equal("Running tools…", mapped.Payload["message"]);

        var roundTripped = MessagePackSerializer.Deserialize<ServerEvent>(
            MessagePackSerializer.Serialize(mapped));
        Assert.Equal("agent.progress", roundTripped.Type);
        Assert.Equal(responseId.ToString(), roundTripped.ResponseId);
        Assert.Equal(operationId.ToString(), Convert.ToString(roundTripped.Payload["operationId"]));
        Assert.Equal("runningTool", Convert.ToString(roundTripped.Payload["kind"]));
        Assert.Equal("started", Convert.ToString(roundTripped.Payload["state"]));
        Assert.Equal("Running tools…", Convert.ToString(roundTripped.Payload["message"]));
    }

    [Fact]
    public void Progress_without_operation_id_maps_null_and_does_not_embed_response_id()
    {
        var mapped = MapProgress(
            ResponseProgressKind.ReadingAttachments,
            ResponseProgressState.Completed,
            responseId: Guid.NewGuid());
        Assert.Null(mapped.Payload["operationId"]);
        Assert.Null(mapped.Payload["message"]);
        Assert.False(mapped.Payload.ContainsKey("responseId"));
        Assert.False(string.IsNullOrWhiteSpace(mapped.ResponseId));
    }

    [Fact]
    public void Session_ready_history_does_not_include_progress()
    {
        var context = Context();
        var historyEntry = new PublicHistoryEntry(
            Guid.NewGuid(),
            1,
            null,
            ConversationRole.Assistant,
            "Hello",
            Guid.NewGuid(),
            EntryStatus.Completed,
            5,
            5,
            SessionMode.Text,
            DateTimeOffset.UtcNow,
            []);
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
            [historyEntry],
            null,
            "browser",
            "browser");
        var mapped = SessionEventMapper.Map(
            new SessionOutput(context, null, new ReadyOutput(ready)),
            Guid.NewGuid(),
            1);

        Assert.Equal("session.ready", mapped.Type);
        Assert.Null(mapped.ResponseId);
        Assert.False(mapped.Payload.ContainsKey("kind"));
        Assert.False(mapped.Payload.ContainsKey("state"));
        Assert.False(mapped.Payload.ContainsKey("operationId"));
        var history = Assert.IsAssignableFrom<IEnumerable<object>>(mapped.Payload["history"]);
        var entry = Assert.Single(history);
        var fields = Assert.IsAssignableFrom<IDictionary<string, object?>>(entry);
        Assert.Equal("Hello", fields["text"]);
        Assert.Equal("assistant", fields["role"]);
        Assert.False(fields.ContainsKey("kind"));
        Assert.False(fields.ContainsKey("state"));
        Assert.False(fields.ContainsKey("operationId"));
        Assert.False(fields.ContainsKey("message"));
        Assert.False(fields.ContainsKey("progress"));
    }

    [Theory]
    [InlineData(nameof(OutputActivity.ProcessingAttachments), "processingAttachments")]
    [InlineData(nameof(OutputActivity.RunningTools), "runningTools")]
    [InlineData(nameof(OutputActivity.WaitingForAgent), "waitingForAgent")]
    [InlineData(nameof(OutputActivity.AgentGenerating), "agentGenerating")]
    [InlineData(nameof(OutputActivity.AgentSpeaking), "agentSpeaking")]
    [InlineData(nameof(OutputActivity.Interrupted), "interrupted")]
    [InlineData(nameof(OutputActivity.Idle), "idle")]
    public void Existing_output_state_camelCase_mapping_is_unchanged(string activity, string expected)
    {
        var mapped = SessionEventMapper.Map(
            new SessionOutput(
                Context(),
                null,
                new StateChangedOutput(
                    SessionStatus.Attached,
                    SessionMode.Text,
                    null,
                    nameof(InputActivity.Listening),
                    activity,
                    false,
                    null)),
            Guid.NewGuid(),
            2);
        Assert.Equal("session.state.changed", mapped.Type);
        Assert.Equal(expected, mapped.Payload["outputState"]);
        Assert.Equal("listening", mapped.Payload["inputState"]);
    }

    private static ServerEvent MapProgress(
        ResponseProgressKind kind,
        ResponseProgressState state,
        Guid? operationId = null,
        string? message = null,
        Guid? responseId = null)
    {
        return SessionEventMapper.Map(
            new SessionOutput(
                Context(),
                responseId ?? Guid.NewGuid(),
                new ResponseProgressOutput(kind, state, operationId, message)),
            Guid.NewGuid(),
            7);
    }

    private static EventContext Context() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-09-15T00:00:02.100Z"),
            Guid.NewGuid(),
            null);
}
