using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

public interface ILocalUserProfileService
{
    ValueTask<UserProfile> GetLocalProfileAsync(CancellationToken cancellationToken = default);

    ValueTask<UserProfile> UpdateLocalProfileAsync(
        long expectedRevision,
        IReadOnlyDictionary<string, string?> values,
        UserProfileValueSource source,
        CancellationToken cancellationToken = default);
}
