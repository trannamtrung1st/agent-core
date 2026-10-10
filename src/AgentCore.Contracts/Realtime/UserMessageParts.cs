using MessagePack;
using System.Text.Json.Serialization;

namespace AgentCore.Contracts.Realtime;

[MessagePackObject, MessagePackFormatter(typeof(UserMessagePartFormatter)), JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UserMessagePartDto
{
    [IgnoreMember, JsonIgnore] public bool InvalidFields { get; set; }
    [Key("kind")] public string Kind { get; set; } = "";
    [Key("text")] public string? Text { get; set; }
    [Key("invocationKind")] public string? InvocationKind { get; set; }
    [Key("skillKey")] public string? SkillKey { get; set; }
    [Key("reference")] public UserResourceReferenceDto? Reference { get; set; }
    [Key("label")] public string? Label { get; set; }
}

[MessagePackObject, MessagePackFormatter(typeof(UserResourceReferenceFormatter)), JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UserResourceReferenceDto
{
    [IgnoreMember, JsonIgnore] public bool InvalidFields { get; set; }
    [Key("kind")] public string Kind { get; set; } = "";
    [Key("agentInstanceId")] public string? AgentInstanceId { get; set; }
    [Key("sessionId")] public string? SessionId { get; set; }
    [Key("itemId")] public string? ItemId { get; set; }
    [Key("artifactId")] public string? ArtifactId { get; set; }
    [Key("agentRunId")] public string? AgentRunId { get; set; }
    [Key("skillKey")] public string? SkillKey { get; set; }
    [Key("selectedRevision")] public long? SelectedRevision { get; set; }
}
