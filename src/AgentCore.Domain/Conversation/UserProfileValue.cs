namespace AgentCore.Domain.Conversation;

public enum UserProfileValueSource
{
    UserSet,
    HostSet,
    ApplicationProfile
}

public sealed record UserProfileValue(
    string Value,
    UserProfileValueSource Source,
    DateTimeOffset UpdatedAt);
