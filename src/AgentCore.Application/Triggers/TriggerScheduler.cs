using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Triggers;

public sealed record TriggerSchedulerPass(
    int Scanned,
    int Admitted,
    int Deduplicated,
    int Stale,
    int NotDue,
    int Expired,
    int Completed,
    int Rejected,
    int Failed,
    IReadOnlyList<Guid> OccurrenceIds);

public sealed class TriggerScheduler
{
    public const int DefaultBatchSize = 100;

    private readonly ITriggerStore _store;
    private readonly ILogger<TriggerScheduler> _logger;
    private readonly int _batchSize;

    public TriggerScheduler(ITriggerStore store, ILogger<TriggerScheduler> logger)
        : this(store, logger, DefaultBatchSize)
    {
    }

    public TriggerScheduler(ITriggerStore store, ILogger<TriggerScheduler> logger, int batchSize)
    {
        _store = store;
        _logger = logger;
        _batchSize = Math.Clamp(batchSize, 1, DefaultBatchSize);
    }

    public async Task<TriggerSchedulerPass> RunOnceAsync(
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
    {
        asOf = TriggerScheduleCalculator.Truncate(asOf);
        var due = await _store.ListDueAsync(asOf, _batchSize, cancellationToken).ConfigureAwait(false);
        var admitted = 0;
        var deduplicated = 0;
        var stale = 0;
        var notDue = 0;
        var expired = 0;
        var completed = 0;
        var rejected = 0;
        var failed = 0;
        var occurrenceIds = new List<Guid>();
        foreach (var registration in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (registration.NextOccurrenceAtUtc is not DateTimeOffset dueAt)
            {
                continue;
            }

            try
            {
                var result = await _store.TryAdmitScheduledAsync(
                    registration.Owner,
                    registration.RegistrationId,
                    registration.ScheduleRevision,
                    dueAt,
                    asOf,
                    cancellationToken).ConfigureAwait(false);
                switch (result.Outcome)
                {
                    case ScheduledAdmitOutcome.Admitted:
                        admitted++;
                        RecordLag(asOf, result);
                        if (result.Occurrence is not null)
                        {
                            occurrenceIds.Add(result.Occurrence.OccurrenceId);
                        }

                        break;
                    case ScheduledAdmitOutcome.Duplicate:
                        deduplicated++;
                        RecordLag(asOf, result);
                        if (result.Occurrence is not null)
                        {
                            occurrenceIds.Add(result.Occurrence.OccurrenceId);
                        }

                        break;
                    case ScheduledAdmitOutcome.Stale:
                        stale++;
                        break;
                    case ScheduledAdmitOutcome.NotDue:
                        notDue++;
                        break;
                    case ScheduledAdmitOutcome.Expired:
                        expired++;
                        break;
                    case ScheduledAdmitOutcome.Completed:
                        completed++;
                        break;
                    default:
                        rejected++;
                        break;
                }

                RuntimeTelemetry.RecordTriggerScheduler(result.Outcome.ToString());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                RuntimeTelemetry.RecordTriggerScheduler("Failed");
                _logger.LogWarning(
                    "Trigger scan failed for registration {RegistrationId} ({ExceptionType}).",
                    registration.RegistrationId,
                    exception.GetType().Name);
            }
        }

        _logger.LogInformation(
            "Trigger scan finished. Scanned {Scanned}, admitted {Admitted}, deduplicated {Deduplicated}, stale {Stale}, expired {Expired}, completed {Completed}, failed {Failed}.",
            due.Count,
            admitted,
            deduplicated,
            stale,
            expired,
            completed,
            failed);
        return new TriggerSchedulerPass(
            due.Count,
            admitted,
            deduplicated,
            stale,
            notDue,
            expired,
            completed,
            rejected,
            failed,
            occurrenceIds);
    }

    private static void RecordLag(DateTimeOffset asOf, ScheduledAdmitResult result)
    {
        if (result.Occurrence?.ScheduledAtUtc is not DateTimeOffset scheduled)
        {
            return;
        }

        RuntimeTelemetry.RecordTriggerDueLag((asOf - scheduled).TotalMilliseconds);
    }
}
