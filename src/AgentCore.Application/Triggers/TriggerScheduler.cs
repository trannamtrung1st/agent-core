using AgentCore.Application.Models;
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
    private readonly ITriggerAdmissionGuard? _guard;
    private readonly IDiagnosticIdSource _diagnostics;
    private readonly IAgentInstanceStore? _instances;
    private readonly IAgentDefinitionStore? _definitions;
    private readonly IModelCatalog? _catalog;
    private readonly int _batchSize;
    private readonly AutomationPresetCatalog? _presets;

    public TriggerScheduler(ITriggerStore store, ILogger<TriggerScheduler> logger)
        : this(store, logger, DefaultBatchSize, null)
    {
    }

    public TriggerScheduler(ITriggerStore store, ILogger<TriggerScheduler> logger, int batchSize)
        : this(store, logger, batchSize, null)
    {
    }

    public TriggerScheduler(ITriggerStore store, ILogger<TriggerScheduler> logger, ITriggerAdmissionGuard guard)
        : this(store, logger, DefaultBatchSize, guard)
    {
    }

    public TriggerScheduler(
        ITriggerStore store,
        ILogger<TriggerScheduler> logger,
        ITriggerAdmissionGuard guard,
        IDiagnosticIdSource diagnostics)
        : this(store, logger, DefaultBatchSize, guard, diagnostics)
    {
    }

    public TriggerScheduler(
        ITriggerStore store,
        ILogger<TriggerScheduler> logger,
        ITriggerAdmissionGuard guard,
        IDiagnosticIdSource diagnostics,
        IAgentInstanceStore instances,
        IAgentDefinitionStore definitions,
        IModelCatalog catalog, AutomationPresetCatalog? presets = null)
        : this(store, logger, DefaultBatchSize, guard, diagnostics, instances, definitions, catalog, presets)
    {
    }

    internal TriggerScheduler(
        ITriggerStore store,
        ILogger<TriggerScheduler> logger,
        IDiagnosticIdSource diagnostics)
        : this(store, logger, DefaultBatchSize, null, diagnostics)
    {
    }

    internal IDiagnosticIdSource DiagnosticIds => _diagnostics;

    private TriggerScheduler(
        ITriggerStore store,
        ILogger<TriggerScheduler> logger,
        int batchSize,
        ITriggerAdmissionGuard? guard,
        IDiagnosticIdSource? diagnostics = null,
        IAgentInstanceStore? instances = null,
        IAgentDefinitionStore? definitions = null,
        IModelCatalog? catalog = null, AutomationPresetCatalog? presets = null)
    {
        _store = store;
        _logger = logger;
        _guard = guard;
        _diagnostics = diagnostics ?? FallbackDiagnosticIdSource.Instance;
        _instances = instances;
        _definitions = definitions;
        _catalog = catalog;
        _presets = presets;
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
                if (_guard is not null)
                {
                    var decision = await _guard.EvaluateAsync(registration.Owner, TriggerSourceKind.Schedule, cancellationToken)
                        .ConfigureAwait(false);
                    if (decision.Kind == TriggerAdmissionDecisionKind.Suspend)
                    {
                        await _store.SuspendPolicyAsync(
                            registration.Owner,
                            registration.AutomationId,
                            registration.Revision,
                            decision.Reason ?? "Scheduling is disabled for this agent.",
                            asOf,
                            cancellationToken).ConfigureAwait(false);
                        rejected++;
                        RuntimeTelemetry.RecordTriggerScheduler("policy");
                        continue;
                    }
                }

                if (registration.Provenance.PresetId is { } presetId && _presets is not null
                    && !(await _presets.OptionsAsync(registration.Owner.AgentInstanceId, cancellationToken, registration.ModelOverrideCatalogKey)).Single(p => p.Template.PresetId == presetId).Eligible)
                {
                    await _store.SuspendPolicyAsync(registration.Owner, registration.AutomationId, registration.Revision,
                        "Preset prerequisites are no longer satisfied.", asOf, cancellationToken);
                    rejected++;
                    continue;
                }

                var result = await _store.TryAdmitScheduledAsync(
                    registration.Owner,
                    registration.AutomationId,
                    registration.TriggerRevision,
                    dueAt,
                    asOf,
                    cancellationToken).ConfigureAwait(false);
                if (result.Occurrence is not null)
                {
                    await PinAdmittedAsync(result.Occurrence, asOf, cancellationToken).ConfigureAwait(false);
                }

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
                DiagnosticLog.Warning(
                    _logger,
                    exception,
                    _diagnostics.NewId(),
                    "Trigger scan failed for registration.",
                    new DiagnosticContext(AutomationId: registration.AutomationId));
            }
        }

        if (due.Count == 0 && failed == 0)
        {
            _logger.LogDebug(
                "Trigger scan finished. Scanned {Scanned}, admitted {Admitted}, deduplicated {Deduplicated}, stale {Stale}, expired {Expired}, completed {Completed}, failed {Failed}.",
                due.Count,
                admitted,
                deduplicated,
                stale,
                expired,
                completed,
                failed);
        }
        else
        {
            _logger.LogInformation(
                "Trigger scan finished. Scanned {Scanned}, admitted {Admitted}, deduplicated {Deduplicated}, stale {Stale}, expired {Expired}, completed {Completed}, failed {Failed}.",
                due.Count,
                admitted,
                deduplicated,
                stale,
                expired,
                completed,
                failed);
        }
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

    private async Task PinAdmittedAsync(
        TriggerOccurrence occurrence,
        DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        if (occurrence.ExecutionTarget.Kind == AutomationExecutionTargetKind.ExistingSession || occurrence.ModelPin is not null || _catalog is null)
        {
            return;
        }

        var decision = await ExecutionModelAdmission.ResolveAsync(
            _catalog,
            _instances,
            _definitions,
            _store,
            occurrence.Owner,
            occurrence.AutomationId,
            cancellationToken).ConfigureAwait(false);
        if (decision is null)
        {
            return;
        }

        if (decision.Pin is null)
        {
            await _store.TryRejectPendingAsync(
                occurrence.OccurrenceId,
                decision.FailureCode ?? ExecutionModelPolicy.UnavailableCode,
                asOf,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await _store.TryAssignModelPinIfMissingAsync(occurrence.OccurrenceId, decision.Pin, cancellationToken)
            .ConfigureAwait(false);
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
