namespace AgentCore.Application.Work;

public sealed class WorkCancellationRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> running = [];

    public CancellationTokenSource Link(Guid workItemId, CancellationToken host)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(host);
        lock (gate)
        {
            if (running.Remove(workItemId, out var previous))
            {
                previous.Dispose();
            }

            running[workItemId] = linked;
        }

        return linked;
    }

    public void Unlink(Guid workItemId, CancellationTokenSource source)
    {
        lock (gate)
        {
            if (running.TryGetValue(workItemId, out var current) && ReferenceEquals(current, source))
            {
                running.Remove(workItemId);
            }
        }

        source.Dispose();
    }

    public void Signal(Guid workItemId)
    {
        CancellationTokenSource? source;
        lock (gate)
        {
            running.TryGetValue(workItemId, out source);
        }

        source?.Cancel();
    }
}
