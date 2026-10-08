using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

/// <summary>
/// Transfers an already claimed run into its sole SessionRuntime mailbox. Dispatch must recheck current
/// owner/tool eligibility and preserve the claim generation. Acknowledgment does not await model work.
/// </summary>
public interface IAgentRunDispatcher
{
    ValueTask<bool> AdmitCompletionAsync(BackgroundCompletionCandidate source, CancellationToken cancellationToken = default);

    ValueTask<bool> RepairPendingInputsAsync(Guid sessionId, CancellationToken cancellationToken = default);

    ValueTask<bool> DispatchAsync(AgentRun run, CancellationToken cancellationToken = default);
}
