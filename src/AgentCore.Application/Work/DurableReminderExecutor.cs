using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Work;

namespace AgentCore.Application.Work;

public sealed class DurableReminderExecutor(
    IWorkItemStore work,
    DurableWorkContextFactory contexts,
    IAgentBrain brain,
    IIdGenerator ids,
    TimeProvider time)
{
    public async ValueTask<int> ExecuteDueAsync(DateTimeOffset asOfUtc, int limit, CancellationToken cancellationToken = default)
    {
        var due = await work.ListRunnableAsync(asOfUtc, limit, cancellationToken).ConfigureAwait(false);
        var ran = 0;
        foreach (var item in due)
        {
            if (item.Provenance.SourceKind != WorkSourceKind.Schedule)
            {
                continue;
            }

            if (await ExecuteAsync(item, asOfUtc, cancellationToken).ConfigureAwait(false))
            {
                ran++;
            }
        }

        return ran;
    }

    private async ValueTask<bool> ExecuteAsync(WorkItem item, DateTimeOffset asOfUtc, CancellationToken cancellationToken)
    {
        var generation = ids.NewId();
        var claimed = await work.TryClaimAsync(
            item.WorkItemId,
            generation,
            asOfUtc,
            asOfUtc.AddMinutes(1),
            cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            return false;
        }

        AgentContext context;
        try
        {
            context = await contexts.CreateAsync(claimed, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentCoreException)
        {
            await FailAsync(claimed, generation, asOfUtc, "context-unavailable", "Pinned context is unavailable.", false, null, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        var decision = await brain.DecideAsync(context, ids.NewId(), cancellationToken).ConfigureAwait(false);
        if (decision is not Speak speak || speak.Request.Tools is not null || context.LanguageModel is null)
        {
            await FailAsync(claimed, generation, asOfUtc, "reminder-invalid", "Scheduled reminder did not produce a tool-free request.", false, null, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        var text = new StringBuilder();
        await foreach (var update in context.LanguageModel.GenerateAsync(speak.Request, cancellationToken).ConfigureAwait(false))
        {
            switch (update)
            {
                case ModelTextDelta delta:
                    text.Append(delta.Text);
                    break;
                case ModelDisplayDelta display:
                    text.Append(display.Text);
                    break;
                case ModelReasoningDelta:
                    break;
                case ModelCompleted:
                    break;
                case ModelToolCallEvent:
                    await FailAsync(claimed, generation, asOfUtc, "unexpected-model-event", "Scheduled reminder received an unsupported model event.", false, null, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                case ModelFailed:
                    await FailAsync(claimed, generation, asOfUtc, "model-unavailable", "The model did not complete the reminder.", true, asOfUtc.AddSeconds(1), cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                default:
                    await FailAsync(claimed, generation, asOfUtc, "unexpected-model-event", "Scheduled reminder received an unsupported model event.", false, null, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
            }
        }

        var result = text.ToString().Trim();
        if (result.Length == 0)
        {
            await FailAsync(claimed, generation, asOfUtc, "empty-result", "Scheduled reminder produced no result.", true, asOfUtc.AddSeconds(1), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        await work.CompleteAsync(claimed.WorkItemId, claimed.Revision, generation, result, time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private ValueTask<WorkItem> FailAsync(
        WorkItem claimed,
        Guid generation,
        DateTimeOffset asOfUtc,
        string code,
        string summary,
        bool replaySafe,
        DateTimeOffset? nextRetryAtUtc,
        CancellationToken cancellationToken) =>
        work.FailAsync(
            claimed.WorkItemId,
            claimed.Revision,
            generation,
            code,
            summary,
            replaySafe,
            asOfUtc,
            nextRetryAtUtc,
            cancellationToken);
}
