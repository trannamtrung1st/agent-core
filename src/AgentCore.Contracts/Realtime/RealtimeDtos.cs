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

[MessagePackObject]
public sealed class ClientCommand
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
    public Dictionary<string, object?> Payload { get; set; } = [];
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
