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
    private readonly SchedulerReadiness? _readiness;

    public TriggerSchedulerHostedService(
        TimeProvider time,
        ILogger<TriggerSchedulerHostedService> logger,
        IDiagnosticIdSource diagnostics, SchedulerReadiness readiness, AgentCore.Application.Security.TenantAutomationService tenantAutomations, AgentCore.Application.Events.TenantEventProcessor tenantEvents)
        : this(
            cancellationToken => RunProductionAsync(logger, diagnostics, cancellationToken, tenantAutomations, tenantEvents),
            time,
            logger,
            diagnostics, readiness)
    {
    }

    internal TriggerSchedulerHostedService(
        Func<CancellationToken, Task> pass,
        TimeProvider time,
        ILogger<TriggerSchedulerHostedService> logger,
        IDiagnosticIdSource diagnostics, SchedulerReadiness? readiness = null)
    {
        _pass = pass;
        _time = time;
        _logger = logger;
        _diagnostics = diagnostics;
        _readiness = readiness;
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
            _readiness?.Record(true);
            RuntimeTelemetry.RecordSchedulerPass(true);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _readiness?.Record(false);
            RuntimeTelemetry.RecordSchedulerPass(false);
            DiagnosticLog.Warning(
                _logger,
                exception,
                _diagnostics.NewId(),
                "Trigger scheduler pass failed.");
        }
    }

    private static async Task RunProductionAsync(
        ILogger<TriggerSchedulerHostedService> logger,
        IDiagnosticIdSource diagnostics,
        CancellationToken cancellationToken, AgentCore.Application.Security.TenantAutomationService tenantAutomations, AgentCore.Application.Events.TenantEventProcessor tenantEvents)
    {
        var succeeded = await RunPhasesAsync([
            async ct => { await tenantAutomations.AdmitDueAsync(ct).ConfigureAwait(false); },
            async ct => { await tenantEvents.RunOnceAsync(ct).ConfigureAwait(false); }
        ], logger, diagnostics, cancellationToken).ConfigureAwait(false);
        if (!succeeded) throw new InvalidOperationException("One or more scheduler phases failed.");
    }

    internal static async Task<bool> RunPhasesAsync(IReadOnlyList<Func<CancellationToken, Task>> phases,
        ILogger<TriggerSchedulerHostedService> logger, IDiagnosticIdSource diagnostics, CancellationToken ct)
    {
        var succeeded = true;
        foreach (var phase in phases)
        {
            ct.ThrowIfCancellationRequested();
            try { await phase(ct).ConfigureAwait(false); }
            catch (Exception exception) when (!ct.IsCancellationRequested && exception is not OutOfMemoryException and not StackOverflowException)
            {
                succeeded = false;
                DiagnosticLog.Warning(logger, exception, diagnostics.NewId(), "Trigger scheduler phase failed; continuing remaining phases.");
            }
        }
        return succeeded;
    }
}
