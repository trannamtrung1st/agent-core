using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
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

            try
            {
                if (await runner.DispatchAsync(claimed, cancellationToken).ConfigureAwait(false))
                {
                    ran++;
                }
            }
            catch (AgentCoreException ex) when (IsMissingSession(ex))
            {
                if (claimed.Claim is { } claim)
                {
                    await store.FailAsync(
                            claimed.ExecutionId,
                            claimed.Revision,
                            claim.Generation,
                            now,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        return ran;
    }

    public ValueTask<IReadOnlyList<ConversationTurnExecution>> ListOpenForSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        store.ListOpenForSessionAsync(sessionId, cancellationToken);

    private static bool IsMissingSession(AgentCoreException exception) =>
        string.Equals(exception.Code, "NotFound", StringComparison.Ordinal)
        && exception.Message.Contains("Session was not found", StringComparison.Ordinal);
}
