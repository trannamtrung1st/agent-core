using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private sealed record CompactionFlight(
        int Generation,
        long RuntimeEpoch,
        string BaseSummary,
        long BaseThrough,
        int BaseFormat);

    private void LaunchCompaction(EventContext cause)
    {
        if (_compactionFlight is not null
            || _deactivated
            || _headlessTransportDetached
            || _activeResponseId is not null
            || _pendingApproval is not null
            || HasPendingUserBatch()
            || LifecycleTransition.IsTerminal(_snapshot)
            || _snapshot.Status is SessionStatus.Ending or SessionStatus.Ended)
        {
            return;
        }

        var lastAssistant = _snapshot.Entries.LastOrDefault(entry => entry.Role == ConversationRole.Assistant);
        if (lastAssistant is null || lastAssistant.Status != EntryStatus.Completed)
        {
            return;
        }

        var generation = ++_compactionGeneration;
        _compactionCts?.Dispose();
        _compactionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _compactionCts.Token;
        var flight = new CompactionFlight(
            generation,
            _snapshot.RuntimeEpoch,
            _snapshot.Summary,
            _snapshot.SummarizedThroughEntrySequence,
            _snapshot.SummaryFormatVersion);
        _compactionFlight = flight;
        var model = ResolveSessionModel(ModelPurpose.Conversation);
        var provenance = ToProvenance(_snapshot.ModelSelection);
        var utc = _time.GetUtcNow();
        var sessionId = SessionId;
        BeginWork();
        _ = Task.Run(async () =>
        {
            try
            {
                var current = flight;
                while (generation == Volatile.Read(ref _compactionGeneration))
                {
                    CompactionOutcome outcome;
                    try
                    {
                        outcome = await new ConversationCompactor(_store).TryCompactAsync(
                                new CompactionCommand(
                                    sessionId,
                                    current.BaseSummary,
                                    current.BaseThrough,
                                    current.BaseFormat,
                                    model,
                                    utc,
                                    provenance),
                                token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        outcome = new CompactionRejected(CompactionRejection.Cancelled);
                    }
                    catch (Exception)
                    {
                        RuntimeTelemetry.RecordDropped("compaction_rejected");
                        return;
                    }

                    if (generation != Volatile.Read(ref _compactionGeneration))
                    {
                        return;
                    }

                    var processed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    BeginWork();
                    if (!TryMailbox(new CompactionReturned(
                            NewContext(cause.EventId),
                            generation,
                            current.RuntimeEpoch,
                            outcome,
                            processed)))
                    {
                        EndWork();
                        RuntimeTelemetry.RecordDropped("compaction_rejected");
                        return;
                    }

                    var again = await processed.Task.ConfigureAwait(false);
                    if (!again || outcome is not CompactionAccepted accepted)
                    {
                        return;
                    }

                    current = current with
                    {
                        BaseSummary = accepted.Candidate.Summary,
                        BaseThrough = accepted.Candidate.ThroughEntrySequence,
                        BaseFormat = accepted.Candidate.FormatVersion
                    };
                }
            }
            finally
            {
                AbandonCompactionFlight(generation);
                EndWork();
            }
        }, CancellationToken.None);
    }

    private bool HandleCompactionReturned(CompactionReturned input)
    {
        if (_compactionFlight is not { } flight
            || input.Generation != flight.Generation
            || input.Generation != _compactionGeneration
            || input.RuntimeEpoch != flight.RuntimeEpoch)
        {
            RuntimeTelemetry.RecordDropped("compaction_stale");
            return false;
        }

        if (input.Outcome is not CompactionAccepted accepted)
        {
            _compactionFlight = null;
            var reason = input.Outcome is CompactionRejected rejected
                ? rejected.Reason
                : CompactionRejection.ProviderFailure;
            RuntimeTelemetry.RecordDropped(
                reason == CompactionRejection.Cancelled ? "compaction_cancelled" : "compaction_rejected");
            return false;
        }

        var candidate = accepted.Candidate;
        if (_pendingApproval is not null
            || _deactivated
            || LifecycleTransition.IsTerminal(_snapshot)
            || _snapshot.Status is SessionStatus.Ending or SessionStatus.Ended
            || !string.Equals(flight.BaseSummary, _snapshot.Summary, StringComparison.Ordinal)
            || flight.BaseThrough != _snapshot.SummarizedThroughEntrySequence
            || flight.BaseFormat != _snapshot.SummaryFormatVersion
            || !string.Equals(candidate.BaseSummary, flight.BaseSummary, StringComparison.Ordinal)
            || candidate.BaseThroughEntrySequence != flight.BaseThrough
            || candidate.ThroughEntrySequence <= flight.BaseThrough
            || candidate.ThroughEntrySequence > _snapshot.DurableLastEntrySequence)
        {
            _compactionFlight = null;
            RuntimeTelemetry.RecordDropped("compaction_stale");
            return false;
        }

        _snapshot = _snapshot with
        {
            Summary = candidate.Summary,
            SummarizedThroughEntrySequence = candidate.ThroughEntrySequence,
            SummaryFormatVersion = candidate.FormatVersion,
            SummaryGeneratedAt = candidate.GeneratedAt,
            SummaryModel = candidate.Model,
            UpdatedAt = _time.GetUtcNow()
        };
        _compactionFlight = flight with
        {
            BaseSummary = candidate.Summary,
            BaseThrough = candidate.ThroughEntrySequence,
            BaseFormat = candidate.FormatVersion
        };
        RequestPersist(_snapshot);
        return true;
    }

    private void AbandonCompactionFlight(int generation)
    {
        if (_compactionGeneration == generation && _compactionFlight?.Generation == generation)
        {
            _compactionFlight = null;
        }
    }

    private void CancelCompaction()
    {
        _compactionGeneration++;
        _compactionFlight = null;
        var cts = _compactionCts;
        if (cts is null)
        {
            return;
        }

        _compactionCts = null;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cts.Dispose();
    }
}
