using AgentCore.Application.Events;

namespace AgentCore.Application.Sessions;

public sealed class SilentSessionOutput : ISessionOutput
{
    public static SilentSessionOutput Instance { get; } = new();

    public ValueTask PublishAsync(SessionOutput output, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
