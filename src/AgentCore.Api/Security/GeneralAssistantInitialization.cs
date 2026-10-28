using AgentCore.Application.Ports;
using AgentCore.Application.Security;
namespace AgentCore.Api.Security;

internal static class GeneralAssistantInitialization
{
    public static async Task ReconcileAsync(IServiceProvider services, CancellationToken ct)
    {
        var agents = services.GetRequiredService<ITenantAgentStore>();
        var assistant = services.GetRequiredService<ManagedGeneralAssistantService>();
        for (var page = 0; ; page++)
        {
            var tenants = await agents.ListAssistantTenantPageAsync(page, 100, ct);
            foreach (var tenant in tenants)
            {
                ct.ThrowIfCancellationRequested();
                await assistant.TryReconcileAsync(tenant, ct);
            }
            if (tenants.Count < 100) break;
        }
    }
}
