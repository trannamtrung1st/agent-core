using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

public interface IProfileLiveUpdateNotifier
{
    ValueTask NotifyProfileUpdatedAsync(UserProfile profile, CancellationToken cancellationToken = default);
}
