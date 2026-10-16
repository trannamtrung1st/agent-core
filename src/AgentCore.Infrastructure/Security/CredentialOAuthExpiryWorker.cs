using AgentCore.Application.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Security;

// This job can only retire expired private proofs. It carries no copied user authority or grant effect.
public sealed class CredentialOAuthExpiryWorker(ITenantCredentialOAuthStore attempts, TimeProvider clock, ILogger<CredentialOAuthExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var expired = await attempts.ExpireCredentialOAuthAttemptsAsync(100, stoppingToken).ConfigureAwait(false);
                if (expired > 0) logger.LogDebug(new EventId(4301, "CredentialOAuthExpired"), "Expired {ExpiredAttemptCount} OAuth authorization attempts.", expired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("OAuth attempt expiry unavailable ({FailureType}).", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
