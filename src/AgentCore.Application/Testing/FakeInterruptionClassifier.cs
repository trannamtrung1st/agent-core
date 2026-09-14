using AgentCore.Application.Ports;

namespace AgentCore.Application.Testing;

public sealed class FakeInterruptionClassifier : IInterruptionClassifier
{
    private readonly InteractionDecision _decision;
    private readonly TaskCompletionSource? _release;

    public FakeInterruptionClassifier(
        InteractionDecision decision = InteractionDecision.Continue,
        TaskCompletionSource? release = null)
    {
        _decision = decision;
        _release = release;
    }

    public int Calls { get; private set; }

    public IReadOnlyList<InterruptionContext> Contexts => _contexts;

    private readonly List<InterruptionContext> _contexts = [];

    private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Called => _called.Task;

    public async ValueTask<InteractionDecision> ClassifyAsync(
        InterruptionContext context,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        _contexts.Add(context);
        _called.TrySetResult();
        if (_release is not null)
        {
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return _decision;
    }
}

public sealed class RecordingAgentBrain(IAgentBrain inner) : IAgentBrain
{
    public int Calls { get; private set; }

    public IReadOnlyList<TriggerKind> Triggers => _triggers;

    private readonly List<TriggerKind> _triggers = [];

    public async ValueTask<AgentDecision> DecideAsync(
        AgentContext context,
        Guid responseId,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        _triggers.Add(context.Trigger.Kind);
        return await inner.DecideAsync(context, responseId, cancellationToken).ConfigureAwait(false);
    }
}
