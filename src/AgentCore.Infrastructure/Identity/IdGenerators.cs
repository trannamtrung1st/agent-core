using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Identity;

public sealed class SystemIdGenerator(TimeProvider time) : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7(time.GetUtcNow());

    public Guid NewSessionId() => Guid.NewGuid();
}

public sealed class DeterministicIdGenerator : IIdGenerator
{
    private readonly object _gate = new();
    private readonly Queue<Guid> _ids;
    private readonly Queue<Guid> _sessionIds;

    public DeterministicIdGenerator(IEnumerable<Guid> ids, IEnumerable<Guid> sessionIds)
    {
        _ids = new Queue<Guid>(ids);
        _sessionIds = new Queue<Guid>(sessionIds);
    }

    public Guid NewId()
    {
        lock (_gate)
        {
            return _ids.Count > 0 ? _ids.Dequeue() : Guid.CreateVersion7();
        }
    }

    public Guid NewSessionId()
    {
        lock (_gate)
        {
            return _sessionIds.Count > 0 ? _sessionIds.Dequeue() : Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        }
    }
}
