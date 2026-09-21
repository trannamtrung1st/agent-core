using MessagePack;

namespace AgentCore.Contracts.Realtime;

[MessagePackObject]
public sealed class CommandAck
{
    [Key("eventId")]
    public string EventId { get; set; } = "";

    [Key("accepted")]
    public bool Accepted { get; set; }

    [Key("error")]
    public CommandError? Error { get; set; }
}

[MessagePackObject]
public sealed class CommandError
{
    [Key("category")]
    public string Category { get; set; } = "Protocol";

    [Key("code")]
    public string Code { get; set; } = "";

    [Key("message")]
    public string Message { get; set; } = "";

    [Key("fatal")]
    public bool Fatal { get; set; }

    [Key("retryAfterMs")]
    public int? RetryAfterMs { get; set; }

    [Key("extensions")]
    public Dictionary<string, object?>? Extensions { get; set; }
}

public interface IRealtimeCommand
{
    int ProtocolVersion { get; }
    string SessionId { get; }
    string EventId { get; }
    long Sequence { get; }
    string? Timestamp { get; }
    string? CorrelationId { get; }
    string? CausationId { get; }
    string? ResponseId { get; }
    string? AttachmentId { get; }
    string Type { get; }
    object? Payload { get; }
}

[MessagePackObject]
public sealed class ClientCommand<TPayload> : IRealtimeCommand
{
    [Key("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [Key("sessionId")]
    public string SessionId { get; set; } = "";

    [Key("eventId")]
    public string EventId { get; set; } = "";

    [Key("sequence")]
    public long Sequence { get; set; }

    [Key("timestamp")]
    public string? Timestamp { get; set; }

    [Key("correlationId")]
    public string? CorrelationId { get; set; }

    [Key("causationId")]
    public string? CausationId { get; set; }

    [Key("responseId")]
    public string? ResponseId { get; set; }

    [Key("attachmentId")]
    public string? AttachmentId { get; set; }

    [Key("type")]
    public string Type { get; set; } = "";

    [Key("payload")]
    public TPayload Payload { get; set; } = default!;

    object? IRealtimeCommand.Payload => Payload;
}

[MessagePackObject]
public sealed class AttachPayload
{
    [Key("lastServerSequence")]
    public long? LastServerSequence { get; set; }

    [Key("ownerCapability")]
    public string? OwnerCapability { get; set; }
}

[MessagePackObject]
public sealed class UserTextPayload
{
    [Key("text")]
    public string Text { get; set; } = "";

    [Key("attachmentIds")]
    public string[]? AttachmentIds { get; set; }

    [Key("behavior")]
    public string? Behavior { get; set; }
}

[MessagePackObject]
public sealed class SetModePayload
{
    [Key("mode")]
    public string Mode { get; set; } = "";
}

[MessagePackObject]
public sealed class SpeechStartedPayload
{
    [Key("streamId")]
    public string StreamId { get; set; } = "";

    [Key("utteranceId")]
    public string UtteranceId { get; set; } = "";

    [Key("sampleOffset")]
    public long SampleOffset { get; set; }

    [Key("activityScore")]
    public double ActivityScore { get; set; }
}

[MessagePackObject]
public sealed class SpeechEndedPayload
{
    [Key("streamId")]
    public string StreamId { get; set; } = "";

    [Key("utteranceId")]
    public string UtteranceId { get; set; } = "";

    [Key("sampleOffset")]
    public long SampleOffset { get; set; }

    [Key("durationMs")]
    public double DurationMs { get; set; }

    [Key("activityScore")]
    public double ActivityScore { get; set; }
}

[MessagePackObject]
public sealed class ClientSpeechEvidencePayload
{
    [Key("kind")]
    public string Kind { get; set; } = "";

    [Key("utteranceId")]
    public string UtteranceId { get; set; } = "";

    [Key("revision")]
    public int? Revision { get; set; }

    [Key("text")]
    public string? Text { get; set; }

    [Key("confidence")]
    public double? Confidence { get; set; }

    [Key("activityScore")]
    public double? ActivityScore { get; set; }

    [Key("durationMs")]
    public double? DurationMs { get; set; }
}

[MessagePackObject]
public sealed class PlaybackPayload
{
    [Key("consumedSamples")]
    public long ConsumedSamples { get; set; }

    [Key("textEndExclusive")]
    public int TextEndExclusive { get; set; }
}

[MessagePackObject]
public sealed class ResponseReceiptPayload
{
    [Key("textEndExclusive")]
    public int TextEndExclusive { get; set; }

    [Key("blockIds")]
    public string[]? BlockIds { get; set; }
}

[MessagePackObject]
public sealed class MutePayload
{
    [Key("muted")]
    public bool Muted { get; set; }
}

[MessagePackObject]
public sealed class EndPayload
{
    [Key("reason")]
    public string Reason { get; set; } = "";
}

[MessagePackObject]
public sealed class CancelResponsePayload
{
}

[MessagePackObject]
public sealed class ApprovalResponsePayload
{
    [Key("approvalId")]
    public string ApprovalId { get; set; } = "";

    [Key("decision")]
    public string Decision { get; set; } = "";
}

[MessagePackObject]
public sealed class ServerEvent
{
    [Key("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [Key("sessionId")]
    public string SessionId { get; set; } = "";

    [Key("attachmentId")]
    public string? AttachmentId { get; set; }

    [Key("eventId")]
    public string EventId { get; set; } = "";

    [Key("sequence")]
    public long Sequence { get; set; }

    [Key("timestamp")]
    public string Timestamp { get; set; } = "";

    [Key("correlationId")]
    public string CorrelationId { get; set; } = "";

    [Key("causationId")]
    public string? CausationId { get; set; }

    [Key("responseId")]
    public string? ResponseId { get; set; }

    [Key("type")]
    public string Type { get; set; } = "";

    [Key("payload")]
    public Dictionary<string, object?> Payload { get; set; } = [];
}

[MessagePackObject]
public sealed class InputAudioDto
{
    [Key("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [Key("sessionId")]
    public string SessionId { get; set; } = "";

    [Key("attachmentId")]
    public string AttachmentId { get; set; } = "";

    [Key("streamId")]
    public string StreamId { get; set; } = "";

    [Key("frameSequence")]
    public long FrameSequence { get; set; }

    [Key("sampleOffset")]
    public long SampleOffset { get; set; }

    [Key("data")]
    public byte[] Data { get; set; } = [];
}

[MessagePackObject]
public sealed class OutputAudioDto
{
    [Key("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [Key("sessionId")]
    public string SessionId { get; set; } = "";

    [Key("attachmentId")]
    public string AttachmentId { get; set; } = "";

    [Key("responseId")]
    public string ResponseId { get; set; } = "";

    [Key("frameSequence")]
    public long FrameSequence { get; set; }

    [Key("sampleOffset")]
    public long SampleOffset { get; set; }

    [Key("isFinal")]
    public bool IsFinal { get; set; }

    [Key("data")]
    public byte[] Data { get; set; } = [];
}
