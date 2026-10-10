using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Application.Sessions;

namespace AgentCore.Api.Mapping;

internal static class UserMessageMapping
{
    private static Guid? Id(string? value) => value is null ? null : Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id
        : throw AgentCoreErrors.Validation("Reference IDs must be non-empty canonical UUIDs.");
    public static IReadOnlyList<UserMessagePart>? FromWire(UserMessagePartDto[]? parts)
    {
        if (parts is null) return null;
        try
        {
            if (parts.Any(p => p is null || p.InvalidFields || p.Reference?.InvalidFields == true)) throw new ArgumentException("Unknown or duplicate structured fields.");
            return UserMessageContent.Normalize(parts.Select(p => new UserMessagePart(p.Kind, p.Text, p.InvocationKind, p.SkillKey, p.Reference is { } r ? new(r.Kind,
                Id(r.AgentInstanceId), Id(r.SessionId), Id(r.ItemId), Id(r.ArtifactId), Id(r.AgentRunId), r.SkillKey, r.SelectedRevision) : null, p.Label)).ToArray(), input: true);
        }
        catch (ArgumentException e) { throw AgentCoreErrors.Validation(e.Message); }
    }
    public static UserMessagePartDto[]? ToWire(IReadOnlyList<UserMessagePart>? parts) => parts?.Select(p => new UserMessagePartDto
    {
        Kind = p.Kind, Text = p.Text, InvocationKind = p.InvocationKind, SkillKey = p.SkillKey, Label = p.Label,
        Reference = p.Reference is { } r ? new UserResourceReferenceDto { Kind = r.Kind, AgentInstanceId = r.AgentInstanceId?.ToString("D"),
            SessionId = r.SessionId?.ToString("D"), ItemId = r.ItemId?.ToString("D"), ArtifactId = r.ArtifactId?.ToString("D"),
            AgentRunId = r.AgentRunId?.ToString("D"), SkillKey = r.SkillKey, SelectedRevision = r.SelectedRevision } : null
    }).ToArray();
}
