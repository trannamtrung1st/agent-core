using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Conversation;

public sealed class ConversationExecutionCoordinator(
    IConversationTurnExecutionStore store,
    IConversationTurnRunner runner,
    IIdGenerator ids,
    TimeProvider time)
{
    public const int DefaultBatchSize = 8;

    public async ValueTask<int> ExecuteRunnableAsync(int limit, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        await store.RecoverExpiredClaimsAsync(now, cancellationToken).ConfigureAwait(false);
        var due = await store.ListRunnableAsync(now, limit, cancellationToken).ConfigureAwait(false);
        if (due.Count == 0)
        {
            return 0;
        }

        var ran = 0;
        foreach (var item in due)
        {
            var generation = ids.NewId();
            var claimed = await store.TryClaimAsync(
                    item.ExecutionId,
                    generation,
                    now,
                    now.AddMinutes(5),
                    cancellationToken)
                .ConfigureAwait(false);
            if (claimed is null)
            {
                continue;
            }

            if (await runner.DispatchAsync(claimed, cancellationToken).ConfigureAwait(false))
            {
                ran++;
            }
        }

        return ran;
    }

    public ValueTask<IReadOnlyList<ConversationTurnExecution>> ListOpenForSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        store.ListOpenForSessionAsync(sessionId, cancellationToken);
}
