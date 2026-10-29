using AgentCore.Application.Observability;
using AgentCore.Application.Ports;

namespace AgentCore.Api;

public sealed class TenantRequestIsolationHostedService(ITenantRequestIsolationStore store, ITenantRequestIsolationControl runtime,
    TimeProvider time, ILogger<TenantRequestIsolationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Guid? cursor = null; using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        do
        {
            try
            {
                using var pass = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); pass.CancelAfter(TimeSpan.FromSeconds(30));
                var page = await store.ListTenantRequestIsolationAsync(cursor, 25, pass.Token).ConfigureAwait(false);
                foreach (var owner in page.Items)
                {
                    try { await runtime.SettleRequestAsync(owner, pass.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { HandledFailureLog.Warning(logger, ex, "request.settlement", "request_settlement_deferred", new(SessionId: owner.SessionId)); }
                }
                cursor = page.NextCursor;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { HandledFailureLog.Warning(logger, ex, "request.settlement", "request_settlement_scan_failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
