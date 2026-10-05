using System.Text;
using AgentCore.Application.Models;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Work;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Work;

public sealed class DurableReminderExecutor(
    IWorkItemStore work,
    DurableWorkContextFactory contexts,
    IAgentBrain brain,
    IIdGenerator ids,
    TimeProvider time,
    WorkCancellationRegistry cancellation,
    SessionToolExecutor tools,
    ILogger<DurableReminderExecutor>? logger = null,
    IWorkCaptureStore? captures = null,
    AgentCore.Application.Experience.ExperienceService? experience = null)
{
    private readonly DurableOccurrenceExecution occurrence = new(tools, time, captures);
    public const string BeforeModelCheckpoint = """{"phase":"before-model"}""";
    public const int DefaultParallelism = 2;

    public async ValueTask<int> ExecuteDueAsync(DateTimeOffset asOfUtc, int limit, CancellationToken cancellationToken = default)
    {
        if (captures is not null)
        {
            await captures.PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
        }

        if (experience is not null)
        {
            try { await experience.ReconcileAsync(cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger?.LogWarning("Secondary retrospective recovery failed; source work remains eligible."); }
        }
        var recovery = await work.RecoverExpiredClaimsAsync(asOfUtc, cancellationToken).ConfigureAwait(false);
        LogRecoveredTerminalFailures(recovery);
        foreach (var resumed in recovery.ObservationResumes)
        {
            await ExecuteResumedAsync(resumed, asOfUtc, cancellationToken).ConfigureAwait(false);
        }
        await work.ExpireDueApprovalsAsync(asOfUtc, cancellationToken).ConfigureAwait(false);
        var due = await work.ListRunnableAsync(asOfUtc, limit, cancellationToken).ConfigureAwait(false);
        var runnable = due
            .Where(item => item.Provenance.SourceKind is WorkSourceKind.Schedule or WorkSourceKind.ApplicationEvent or WorkSourceKind.Retrospection or WorkSourceKind.ThoughtActivation)
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
        if (updated.IsTerminal && experience is not null) await experience.TryWorkBoundaryAsync(updated, CancellationToken.None);
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

        return await ContinueClaimedAsync(claimed, generation, asOfUtc, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<bool> ExecuteResumedAsync(
        WorkItem claimed,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        if (claimed.Claim is null)
        {
            return ValueTask.FromResult(false);
        }

        return ContinueClaimedAsync(claimed, claimed.Claim.Generation, asOfUtc, cancellationToken);
    }

    private async ValueTask<bool> ContinueClaimedAsync(
        WorkItem item,
        Guid generation,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var linked = cancellation.Link(item.WorkItemId, cancellationToken);
        try
        {
            var running = await work.RenewClaimAsync(
                item.WorkItemId,
                item.Revision,
                generation,
                asOfUtc.AddMinutes(1),
                asOfUtc,
                linked.Token).ConfigureAwait(false);
            if (!DurableToolCallCheckpoint.TryRead(running.Checkpoint, out _))
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

            if (running.Provenance.SourceKind == WorkSourceKind.Retrospection)
            {
                try
                {
                    if (experience is null) throw AgentCoreErrors.Validation("Experience is unavailable.");
                    using var derivedCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    using var derivedTimer = time.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(),
                        derivedCts, ToolLimits.Overall, Timeout.InfiniteTimeSpan);
                    var derivedResult = await experience.GenerateAsync(running, derivedCts.Token);
                    if (await TryCommitCancellationAsync(item.Provenance.SourceOccurrenceId, generation)) return true;
                    await work.CompleteAsync(running.WorkItemId, running.Revision, generation, derivedResult, time.GetUtcNow(), CancellationToken.None);
                }
                catch (Exception exception) when (!linked.Token.IsCancellationRequested)
                {
                    var unavailable = exception is AgentCoreException { StatusCode: 404 };
                    var invalid = exception is AgentCoreException { StatusCode: 400 };
                    var now = time.GetUtcNow();
                    await FailAsync(running, generation, now, unavailable ? "source-unavailable" : invalid ? "invalid-retrospective" : "retrospective-failed",
                        unavailable ? "Source checkpoint is unavailable." : invalid ? "Retrospective output or model is invalid." : "Retrospection generation failed.",
                        !unavailable && !invalid, !unavailable && !invalid ? now.Add(RetryDelay(running.AttemptCount)) : null, CancellationToken.None, exception);
                }
                return true;
            }

            AgentContext context;
            try
            {
                context = await contexts.CreateAsync(running, linked.Token).ConfigureAwait(false);
            }
            catch (AgentCoreException exception)
            {
                var modelUnavailable = exception.Message.Contains("Pinned model is unavailable", StringComparison.Ordinal);
                await FailAsync(
                        running,
                        generation,
                        asOfUtc,
                        modelUnavailable ? ExecutionModelPolicy.UnavailableCode : "context-unavailable",
                        modelUnavailable ? "Pinned model is unavailable." : "Pinned context is unavailable.",
                        modelUnavailable,
                        modelUnavailable ? asOfUtc.Add(RetryDelay(running.AttemptCount)) : null,
                        CancellationToken.None,
                        exception)
                    .ConfigureAwait(false);
                return true;
            }

            if (item.Provenance.SourceKind is WorkSourceKind.ApplicationEvent or WorkSourceKind.ThoughtActivation
                || (running.SideEffect.Disposition is WorkSideEffectDisposition.InFlight or WorkSideEffectDisposition.Indeterminate
                    && running.Checkpoint?.PayloadJson.Contains("\"ObservationRequired\":true", StringComparison.Ordinal) == true))
            {
                return await ExecuteApplicationAsync(item, running, generation, asOfUtc, context, linked.Token)
                    .ConfigureAwait(false);
            }

            var decision = await brain.DecideAsync(context, ids.NewId(), linked.Token).ConfigureAwait(false);
            if (decision is Speak scheduledTools
                && scheduledTools.Request.Tools is { Count: > 0 }
                && context.LanguageModel is not null)
            {
                return await ExecuteApplicationAsync(
                    item,
                    running,
                    generation,
                    asOfUtc,
                    context,
                    linked.Token,
                    scheduledTools).ConfigureAwait(false);
            }

            if (decision is not Speak speak || speak.Request.Tools is not null || context.LanguageModel is null)
            {
                await FailAsync(running, generation, asOfUtc, "reminder-invalid", "Scheduled reminder did not produce a tool-free request.", false, null, CancellationToken.None)
                    .ConfigureAwait(false);
                return true;
            }

            var text = new StringBuilder();
            using var modelCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            using var modelTimer = time.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(),
                modelCts,
                ToolLimits.Overall,
                Timeout.InfiniteTimeSpan);
            try
            {
                await foreach (var update in context.LanguageModel.GenerateAsync(speak.Request, modelCts.Token).ConfigureAwait(false))
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
            }
            catch (OperationCanceledException exception) when (!linked.Token.IsCancellationRequested)
            {
                var failedAtUtc = time.GetUtcNow();
                await FailAsync(
                    running,
                    generation,
                    failedAtUtc,
                    "model-timeout",
                    "The model did not finish within the reminder budget.",
                    true,
                    failedAtUtc.Add(RetryDelay(running.AttemptCount)),
                    CancellationToken.None,
                    exception).ConfigureAwait(false);
                return true;
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
                    "The model returned no result.",
                    true,
                    asOfUtc.Add(RetryDelay(running.AttemptCount)),
                    CancellationToken.None).ConfigureAwait(false);
                return true;
            }

            await work.CompleteAsync(running.WorkItemId, running.Revision, generation, result, asOfUtc, CancellationToken.None)
                .ConfigureAwait(false);
            await NoteTerminalAsync(running.WorkItemId, asOfUtc).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        Speak? decided = null)
    {
        var speak = decided;
        if (speak is null)
        {
            var decision = await brain.DecideAsync(context, ids.NewId(), cancellationToken).ConfigureAwait(false);
            if (decision is not Speak fresh || context.LanguageModel is null)
            {
                await FailAsync(running, generation, asOfUtc, "event-invalid", "Application event did not produce a model request.", false, null, CancellationToken.None)
                    .ConfigureAwait(false);
                return true;
            }

            speak = fresh;
        }

        if (context.LanguageModel is null)
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
            cancellationToken,
            context.TrustedConnection).ConfigureAwait(false);
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
                    CancellationToken.None,
                    completed.AttentionRequired).ConfigureAwait(false);
                await NoteTerminalAsync(completed.Running.WorkItemId, asOfUtc).ConfigureAwait(false);
                if (experience is not null && await work.GetAsync(completed.Running.Owner, completed.Running.WorkItemId) is { } terminal)
                    await experience.TryWorkBoundaryAsync(terminal, CancellationToken.None);
                RuntimeTelemetry.RecordWork("completed");
                break;
            case DurableOccurrenceRetry retry:
                var failedAtUtc = retry.Code == "model-timeout" ? time.GetUtcNow() : asOfUtc;
                await FailAsync(
                    retry.Running,
                    generation,
                    failedAtUtc,
                    retry.Code,
                    retry.Summary,
                    true,
                    failedAtUtc.Add(RetryDelay(retry.Running.AttemptCount)),
                    CancellationToken.None,
                    retry.Error).ConfigureAwait(false);
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
        await NoteTerminalAsync(current.WorkItemId, time.GetUtcNow()).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        Exception? error = null)
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
        if (failed.Failure?.DiagnosticId is Guid diagnosticId
            && diagnosticId != Guid.Empty
            && logger is not null)
        {
            if (failed.Status == WorkItemStatus.WaitingToRetry)
            {
                DiagnosticLog.Warning(
                    logger,
                    error ?? new InvalidOperationException(failed.Failure.Summary),
                    diagnosticId,
                    $"Work item will retry. Attempt {failed.AttemptCount} of {failed.MaxAttempts}.",
                    new DiagnosticContext(
                        SessionId: failed.Provenance.SourceSessionId,
                        TriggerRegistrationId: failed.Provenance.RegistrationId,
                        TriggerOccurrenceId: failed.Provenance.SourceOccurrenceId,
                        WorkItemId: failed.WorkItemId,
                        ErrorCategory: "work",
                        ErrorCode: failed.Failure.Code));
            }
            else if (failed.Status == WorkItemStatus.Failed)
            {
                DiagnosticLog.Error(
                    logger,
                    error,
                    diagnosticId,
                    "Work item failed.",
                    new DiagnosticContext(
                        SessionId: failed.Provenance.SourceSessionId,
                        TriggerRegistrationId: failed.Provenance.RegistrationId,
                        TriggerOccurrenceId: failed.Provenance.SourceOccurrenceId,
                        WorkItemId: failed.WorkItemId,
                        ErrorCategory: "work",
                        ErrorCode: failed.Failure.Code));
            }
        }

        if (failed.Status is WorkItemStatus.Failed or WorkItemStatus.Cancelled)
        {
            await NoteTerminalAsync(failed.WorkItemId, asOfUtc).ConfigureAwait(false);
            if (experience is not null) await experience.TryWorkBoundaryAsync(failed, CancellationToken.None);
        }

        RuntimeTelemetry.RecordWork(failed.Status == WorkItemStatus.WaitingToRetry ? "retry" : "failed");
        return failed;
    }

    private async ValueTask NoteTerminalAsync(Guid workItemId, DateTimeOffset terminalAt)
    {
        if (captures is not null) await captures.ExtendRetentionAsync(workItemId, terminalAt);
    }

    private void LogRecoveredTerminalFailures(ExpiredClaimRecovery recovery)
    {
        if (logger is null)
        {
            return;
        }

        foreach (var failed in recovery.TerminalFailures)
        {
            if (failed.Status != WorkItemStatus.Failed
                || failed.Failure?.DiagnosticId is not Guid diagnosticId
                || diagnosticId == Guid.Empty)
            {
                continue;
            }

            DiagnosticLog.Error(
                logger,
                null,
                diagnosticId,
                "Work item failed.",
                new DiagnosticContext(
                    SessionId: failed.Provenance.SourceSessionId,
                    TriggerRegistrationId: failed.Provenance.RegistrationId,
                    TriggerOccurrenceId: failed.Provenance.SourceOccurrenceId,
                    WorkItemId: failed.WorkItemId,
                    ErrorCategory: "work",
                    ErrorCode: failed.Failure.Code));
        }
    }
}
