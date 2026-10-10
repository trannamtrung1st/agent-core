using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api;

public sealed class TriggerSchedulerHostedService : BackgroundService
{
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

    private readonly Func<CancellationToken, Task> _pass;
    private readonly TimeProvider _time;
    private readonly ILogger<TriggerSchedulerHostedService> _logger;
    private readonly IDiagnosticIdSource _diagnostics;

    public TriggerSchedulerHostedService(
        TriggerScheduler scheduler,
        TriggerOccurrenceRouter router,
        ExternalEventIngress events,
        TimeProvider time,
        ILogger<TriggerSchedulerHostedService> logger,
        IDiagnosticIdSource diagnostics, CoreEventDispatcher coreEvents)
        : this(
            cancellationToken => RunProductionAsync(scheduler, router, events, coreEvents, time, logger, diagnostics, cancellationToken),
            time,
            logger,
            diagnostics)
    {
    }

    internal TriggerSchedulerHostedService(
        Func<CancellationToken, Task> pass,
        TimeProvider time,
        ILogger<TriggerSchedulerHostedService> logger,
        IDiagnosticIdSource diagnostics)
    {
        _pass = pass;
        _time = time;
        _logger = logger;
        _diagnostics = diagnostics;
    }

    internal IDiagnosticIdSource DiagnosticIds => _diagnostics;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(Cadence, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task RunPassAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _pass(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning(
                _logger,
                exception,
                _diagnostics.NewId(),
                "Trigger scheduler pass failed.");
        }
    }

    private static async Task RunProductionAsync(
        TriggerScheduler scheduler,
        TriggerOccurrenceRouter router,
        ExternalEventIngress events,
        CoreEventDispatcher coreEvents,
        TimeProvider time,
        ILogger<TriggerSchedulerHostedService> logger,
        IDiagnosticIdSource diagnostics,
        CancellationToken cancellationToken)
    {
        await RunPhasesAsync([
            async ct => { await coreEvents.RunOnceAsync(ct).ConfigureAwait(false); },
            async ct => { await events.ResumePendingAsync(ct).ConfigureAwait(false); },
            async ct => { await scheduler.RunOnceAsync(time.GetUtcNow(), ct).ConfigureAwait(false); },
            async ct => { await router.RouteOnceAsync(ct).ConfigureAwait(false); }
        ], logger, diagnostics, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RunPhasesAsync(IReadOnlyList<Func<CancellationToken, Task>> phases,
        ILogger<TriggerSchedulerHostedService> logger, IDiagnosticIdSource diagnostics, CancellationToken ct)
    {
        foreach (var phase in phases)
        {
            ct.ThrowIfCancellationRequested();
            try { await phase(ct).ConfigureAwait(false); }
            catch (Exception exception) when (!ct.IsCancellationRequested && exception is not OutOfMemoryException and not StackOverflowException)
            {
                DiagnosticLog.Warning(logger, exception, diagnostics.NewId(), "Trigger scheduler phase failed; continuing remaining phases.");
            }
        }
    }
}
