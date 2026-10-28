using AgentCore.Application.Ports;
using AgentCore.Application.Security;
namespace AgentCore.Api.Security;

internal static class GeneralAssistantInitialization
{
    public static async Task ReconcileAsync(IServiceProvider services, CancellationToken ct)
    {
        var agents = services.GetRequiredService<ITenantAgentStore>();
        var assistant = services.GetRequiredService<ManagedGeneralAssistantService>();
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("GeneralAssistantInitialization");
        for (var page = 0; ; page++)
        {
            var tenants = await agents.ListAssistantTenantPageAsync(page, 100, ct);
            foreach (var tenant in tenants)
            {
                ct.ThrowIfCancellationRequested();
                try { await assistant.ReconcileAsync(tenant, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    var reason = error is SecurityConflictException conflict ? conflict.ReasonCode
                        : error is AgentCore.Application.Sessions.AgentCoreException application ? application.Code : "configuration_incompatible";
                    log.LogError("General Assistant setup requires attention for tenant {TenantId}; reason {ReasonCode}. Review current configuration or restore the matching release. New managed work remains fenced.", tenant, reason);
                }
            }
            if (tenants.Count < 100) break;
        }
    }
}
