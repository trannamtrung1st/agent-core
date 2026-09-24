using System.Text;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Work;

namespace AgentCore.Application.Work;

public sealed class DurableReminderExecutor(
    IWorkItemStore work,
    DurableWorkContextFactory contexts,
    IAgentBrain brain,
    IIdGenerator ids,
    TimeProvider time,
    WorkCancellationRegistry cancellation,
    SessionToolExecutor tools)
{
    private readonly DurableOccurrenceExecution occurrence = new(tools, time);
    public const string BeforeModelCheckpoint = """{"phase":"before-model"}""";
    public const int DefaultParallelism = 2;

    public async ValueTask<int> ExecuteDueAsync(DateTimeOffset asOfUtc, int limit, CancellationToken cancellationToken = default)
    {
        await work.RecoverExpiredClaimsAsync(asOfUtc, cancellationToken).ConfigureAwait(false);
        await work.ExpireDueApprovalsAsync(asOfUtc, cancellationToken).ConfigureAwait(false);
        var due = await work.ListRunnableAsync(asOfUtc, limit, cancellationToken).ConfigureAwait(false);
        var runnable = due
            .Where(item => item.Provenance.SourceKind is WorkSourceKind.Schedule or WorkSourceKind.ApplicationEvent)
            .ToArray();
        if (runnable.Length == 0)
        {
            return 0;
        }

        var ran = 0;
        await Parallel.ForEachAsync(
            runnable,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = DefaultParallelism,
                CancellationToken = cancellationToken
            },
            async (item, ct) =>
            {
                if (await ExecuteAsync(item, asOfUtc, ct).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref ran);
                }
            }).ConfigureAwait(false);

        return ran;
    }

    public async ValueTask<WorkItem> RequestCancellationAsync(
        WorkOwner owner,
        Guid workItemId,
        long expectedRevision,
        string? knownEffectSummary,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var current = await work.GetAsync(owner, workItemId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            throw AgentCoreErrors.NotFound("Work item was not found.");
        }

        var updated = await work.RequestCancellationAsync(
            owner,
            workItemId,
            expectedRevision,
            WorkCancellationSemantics.MergeKnownEffectSummary(current, knownEffectSummary),
            requestedAtUtc,
            cancellationToken).ConfigureAwait(false);
        cancellation.Signal(workItemId);
        return updated;
    }

    internal static TimeSpan RetryDelay(int attemptCount)
    {
        var shift = Math.Clamp(attemptCount, 1, 4) - 1;
        return TimeSpan.FromSeconds(1 << shift);
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

        var linked = cancellation.Link(item.WorkItemId, cancellationToken);
        try
        {
            var running = await work.RenewClaimAsync(
                claimed.WorkItemId,
                claimed.Revision,
                generation,
                asOfUtc.AddMinutes(1),
                asOfUtc,
                linked.Token).ConfigureAwait(false);
            if (!DurableTurnCheckpoint.TryRead(running.Checkpoint, out _))
            {
                running = await work.CheckpointAsync(
                    running.WorkItemId,
                    running.Revision,
                    generation,
                    new WorkCheckpoint(BeforeModelCheckpoint, 0, 0, 0),
                    null,
                    asOfUtc,
                    linked.Token).ConfigureAwait(false);
            }

            AgentContext context;
            try
            {
                context = await contexts.CreateAsync(running, linked.Token).ConfigureAwait(false);
            }
            catch (AgentCoreException)
            {
                await FailAsync(running, generation, asOfUtc, "context-unavailable", "Pinned context is unavailable.", false, null, CancellationToken.None)
                    .ConfigureAwait(false);
                return true;
            }

            if (item.Provenance.SourceKind == WorkSourceKind.ApplicationEvent)
            {
                return await ExecuteApplicationAsync(item, running, generation, asOfUtc, context, linked.Token)
                    .ConfigureAwait(false);
            }

            var decision = await brain.DecideAsync(context, ids.NewId(), linked.Token).ConfigureAwait(false);
            if (decision is not Speak speak || speak.Request.Tools is not null || context.LanguageModel is null)
            {
                await FailAsync(running, generation, asOfUtc, "reminder-invalid", "Scheduled reminder did not produce a tool-free request.", false, null, CancellationToken.None)
                    .ConfigureAwait(false);
                return true;
            }

            var text = new StringBuilder();
            await foreach (var update in context.LanguageModel.GenerateAsync(speak.Request, linked.Token).ConfigureAwait(false))
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
                        await FailAsync(running, generation, asOfUtc, "unexpected-model-event", "Scheduled reminder received an unsupported model event.", false, null, CancellationToken.None)
                            .ConfigureAwait(false);
                        return true;
                    case ModelFailed:
                        await FailAsync(
                            running,
                            generation,
                            asOfUtc,
                            "model-unavailable",
                            "The model did not complete the reminder.",
                            true,
                            asOfUtc.Add(RetryDelay(running.AttemptCount)),
                            CancellationToken.None).ConfigureAwait(false);
                        return true;
                    default:
                        await FailAsync(running, generation, asOfUtc, "unexpected-model-event", "Scheduled reminder received an unsupported model event.", false, null, CancellationToken.None)
                            .ConfigureAwait(false);
                        return true;
                }
            }

            if (await TryCommitCancellationAsync(item.Provenance.SourceOccurrenceId, generation).ConfigureAwait(false))
            {
                return true;
            }

            var result = text.ToString().Trim();
            if (result.Length == 0)
            {
                await FailAsync(
                    running,
                    generation,
                    asOfUtc,
                    "empty-result",
                    "Scheduled reminder produced no result.",
                    true,
                    asOfUtc.Add(RetryDelay(running.AttemptCount)),
                    CancellationToken.None).ConfigureAwait(false);
                return true;
            }

            await work.CompleteAsync(running.WorkItemId, running.Revision, generation, result, asOfUtc, CancellationToken.None)
                .ConfigureAwait(false);
            RuntimeTelemetry.RecordWork("completed");
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!await TryCommitCancellationAsync(item.Provenance.SourceOccurrenceId, generation).ConfigureAwait(false))
            {
                throw;
            }

            return true;
        }
        finally
        {
            cancellation.Unlink(item.WorkItemId, linked);
        }
    }

    private async ValueTask<bool> ExecuteApplicationAsync(
        WorkItem item,
        WorkItem running,
        Guid generation,
        DateTimeOffset asOfUtc,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        var decision = await brain.DecideAsync(context, ids.NewId(), cancellationToken).ConfigureAwait(false);
        if (decision is not Speak speak || context.LanguageModel is null)
        {
            await FailAsync(running, generation, asOfUtc, "event-invalid", "Application event did not produce a model request.", false, null, CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }

        var outcome = await occurrence.RunAsync(
            running,
            speak.Request,
            context.LanguageModel,
            context.Definition,
            context.Trigger.Kind,
            (current, saved, token) => work.CheckpointAsync(
                current.WorkItemId,
                current.Revision,
                generation,
                saved,
                null,
                asOfUtc,
                token),
            work,
            generation,
            asOfUtc,
            ids,
            cancellationToken).ConfigureAwait(false);
        if (await TryCommitCancellationAsync(item.Provenance.SourceOccurrenceId, generation).ConfigureAwait(false))
        {
            return true;
        }

        switch (outcome)
        {
            case DurableOccurrenceCompleted completed:
                await work.CompleteAsync(
                    completed.Running.WorkItemId,
                    completed.Running.Revision,
                    generation,
                    completed.Text,
                    asOfUtc,
                    CancellationToken.None).ConfigureAwait(false);
                RuntimeTelemetry.RecordWork("completed");
                break;
            case DurableOccurrenceRetry retry:
                await FailAsync(
                    retry.Running,
                    generation,
                    asOfUtc,
                    retry.Code,
                    retry.Summary,
                    true,
                    asOfUtc.Add(RetryDelay(retry.Running.AttemptCount)),
                    CancellationToken.None).ConfigureAwait(false);
                break;
            case DurableOccurrenceSuspended:
                RuntimeTelemetry.RecordWork("waiting");
                break;
            case DurableOccurrenceFailed failed:
                await FailAsync(
                    failed.Running,
                    generation,
                    asOfUtc,
                    failed.Code,
                    failed.Summary,
                    false,
                    null,
                    CancellationToken.None).ConfigureAwait(false);
                break;
        }

        return true;
    }

    private async ValueTask<bool> TryCommitCancellationAsync(Guid occurrenceId, Guid generation)
    {
        var current = await work.GetBySourceOccurrenceAsync(occurrenceId, CancellationToken.None).ConfigureAwait(false);
        if (current is not { CancellationRequested: true })
        {
            return false;
        }

        if (current.Status == WorkItemStatus.Cancelled)
        {
            return true;
        }

        if (current.Status != WorkItemStatus.Running)
        {
            return true;
        }

        await work.CommitCancellationAsync(
            current.WorkItemId,
            current.Revision,
            generation,
            WorkCancellationSemantics.MergeKnownEffectSummary(current, current.KnownEffectSummary),
            time.GetUtcNow(),
            CancellationToken.None).ConfigureAwait(false);
        RuntimeTelemetry.RecordWork("cancelled");
        return true;
    }

    private async ValueTask<WorkItem> FailAsync(
        WorkItem running,
        Guid generation,
        DateTimeOffset asOfUtc,
        string code,
        string summary,
        bool replaySafe,
        DateTimeOffset? nextRetryAtUtc,
        CancellationToken cancellationToken)
    {
        var failed = await work.FailAsync(
            running.WorkItemId,
            running.Revision,
            generation,
            code,
            summary,
            replaySafe,
            asOfUtc,
            nextRetryAtUtc,
            cancellationToken).ConfigureAwait(false);
        RuntimeTelemetry.RecordWork(failed.Status == WorkItemStatus.WaitingToRetry ? "retry" : "failed");
        return failed;
    }
}
