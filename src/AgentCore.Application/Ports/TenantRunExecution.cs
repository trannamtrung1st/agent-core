namespace AgentCore.Application.Ports;

public interface ITenantRunDispatcher : IAgentRunDispatcher;
public sealed record TenantRunCancellation(AgentCore.Domain.Conversation.AgentRunOwner Owner, Guid SessionId, Guid RunId, Guid? Generation,
    TenantSessionInitiator Initiator, long ExpectedSecurityRevision, long ExpectedRunRevision);
public interface ITenantRunControl
{
    ValueTask<AgentCore.Domain.Conversation.AgentRun> CancelAsync(TenantRunCancellation request, CancellationToken ct = default);
}
public interface ITenantRunExecution
{
    ValueTask<int> ExecuteRunnableAsync(int limit, CancellationToken ct = default);
}
