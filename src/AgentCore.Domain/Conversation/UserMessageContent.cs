using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Conversation;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UserResourceReference(string Kind, Guid? AgentInstanceId = null, Guid? SessionId = null,
    Guid? ItemId = null, Guid? ArtifactId = null, Guid? AgentRunId = null, string? SkillKey = null,
    long? SelectedRevision = null)
{
    [JsonIgnore]
    public string Locator => Kind switch
    {
        "homeFile" => $"homeFile:{AgentInstanceId:D}/{ItemId:D}",
        "session" or "backgroundSession" => $"{Kind}:{SessionId:D}",
        "artifact" => $"artifact:{SessionId:D}/{ArtifactId:D}",
        "agentRun" => $"agentRun:{SessionId:D}/{AgentRunId:D}",
        "skill" => $"skill:{AgentInstanceId:D}/{SkillKey}",
        _ => throw new ArgumentException("Unknown reference kind.")
    };

    public void Validate()
    {
        static bool Id(Guid? id) => id is { } value && value != Guid.Empty;
        var valid = Kind switch
        {
            "homeFile" => Id(AgentInstanceId) && Id(ItemId) && SessionId is null && ArtifactId is null && AgentRunId is null && SkillKey is null,
            "session" or "backgroundSession" => Id(SessionId) && AgentInstanceId is null && ItemId is null && ArtifactId is null && AgentRunId is null && SkillKey is null,
            "artifact" => Id(SessionId) && Id(ArtifactId) && AgentInstanceId is null && ItemId is null && AgentRunId is null && SkillKey is null && SelectedRevision is null,
            "agentRun" => Id(SessionId) && Id(AgentRunId) && AgentInstanceId is null && ItemId is null && ArtifactId is null && SkillKey is null,
            "skill" => Id(AgentInstanceId) && SkillKeys.IsValid(SkillKey) && SessionId is null && ItemId is null && ArtifactId is null && AgentRunId is null,
            _ => false
        };
        if (!valid || SelectedRevision is <= 0) throw new ArgumentException("Reference requires its exact typed locator fields and a positive optional revision.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UserMessagePart(string Kind, string? Text = null, string? InvocationKind = null,
    string? SkillKey = null, UserResourceReference? Reference = null, string? Label = null);

public static class UserMessageContent
{
    public const int MaxTextUnits = 8000;
    public const int MaxParts = 128;
    public const int MaxPartsBytes = 32 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<UserMessagePart>? Normalize(IReadOnlyList<UserMessagePart>? parts, bool input = false)
    {
        if (parts is null) return null;
        if (parts.Count is < 1 or > MaxParts) throw new ArgumentException("Message must contain 1 to 128 parts.");
        var result = new List<UserMessagePart>();
        var skills = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (part is null || part.Label is { Length: > 200 } || input && part.Label is not null)
                throw new ArgumentException("Part labels are bounded server metadata, not user input.");
            switch (part.Kind)
            {
                case "text" when part.Text is { Length: > 0 } && part.InvocationKind is null && part.SkillKey is null && part.Reference is null && part.Label is null:
                    if (result.LastOrDefault() is { Kind: "text" } previous) result[^1] = previous with { Text = previous.Text + part.Text };
                    else result.Add(part);
                    break;
                case "invocation" when part.InvocationKind == "skill" && SkillKeys.IsValid(part.SkillKey) && part.Text is null && part.Reference is null:
                    if (skills.Add(part.SkillKey!)) result.Add(part);
                    break;
                case "reference" when part.Reference is not null && part.Text is null && part.SkillKey is null && part.InvocationKind is null:
                    part.Reference.Validate(); result.Add(part); break;
                default: throw new ArgumentException("Invalid or ambiguous message part.");
            }
        }
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(WithoutLabels(result), Json)) > MaxPartsBytes || DisplayText(result).Length > MaxTextUnits)
            throw new ArgumentException("Structured message exceeds 8000 text units or 32 KiB of parts.");
        return Array.AsReadOnly(result.ToArray());
    }

    public static string DisplayText(IReadOnlyList<UserMessagePart> parts) => string.Concat(parts.Select(part => part.Kind switch
    { "text" => part.Text, "invocation" => "/" + part.SkillKey, "reference" => "@" + part.Reference!.Locator, _ => throw new ArgumentException("Invalid message part.") }));
    public static IReadOnlyList<string> ExplicitSkills(IReadOnlyList<UserMessagePart>? parts) =>
        (parts ?? []).Where(part => part.Kind == "invocation").Select(part => part.SkillKey!).Distinct(StringComparer.Ordinal).ToArray();
    public static bool HasTask(IReadOnlyList<UserMessagePart>? parts) => parts is null || parts.Any(part => part.Kind == "reference" || part.Kind == "text" && !string.IsNullOrWhiteSpace(part.Text));
    public static IReadOnlyList<UserMessagePart>? WithoutLabels(IReadOnlyList<UserMessagePart>? parts) => parts?.Select(part => part with { Label = null }).ToArray();
}

public sealed record ComposerReferenceEvidence(UserResourceReference Reference, string Label, string Status,
    string Text, long? Revision = null, string? Sha256 = null, bool Truncated = false);
public sealed record ComposerRunInput(IReadOnlyList<string> ExplicitSkillKeys,
    IReadOnlyList<ComposerReferenceEvidence> References, DateTimeOffset AdmittedAtUtc, string? Error = null);
