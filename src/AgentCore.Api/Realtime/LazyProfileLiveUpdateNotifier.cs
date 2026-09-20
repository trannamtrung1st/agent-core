using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

internal sealed class LazyProfileLiveUpdateNotifier(IServiceProvider services) : IProfileLiveUpdateNotifier
{
    public ValueTask NotifyProfileUpdatedAsync(UserProfile profile, CancellationToken cancellationToken = default) =>
        services.GetRequiredService<SessionHost>().NotifyProfileUpdatedAsync(profile, cancellationToken);
}
