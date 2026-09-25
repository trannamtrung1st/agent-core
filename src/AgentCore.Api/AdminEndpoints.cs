using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
namespace AgentCore.Api;

internal static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/admin").AddEndpointFilter<OwnerCapabilityFilter>();

        group.MapGet("/definitions", async (
            AdminReadService admin,
            CancellationToken cancellationToken) =>
        {
            var items = await admin.ListDefinitionsAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(new AdminDefinitionInventoryResponse(items.Select(AdminHttpMapping.ToDefinitionItem).ToArray()));
        });

        group.MapGet("/instances", async (
            AdminReadService admin,
            CancellationToken cancellationToken) =>
        {
            var items = await admin.ListInstancesAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(new AdminInstanceInventoryResponse(items.Select(AdminHttpMapping.ToInstanceItem).ToArray()));
        });

        group.MapGet("/instances/{instanceId:guid}/effective-config", async (
            Guid instanceId,
            AdminReadService admin,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var config = await admin.GetEffectiveConfigurationAsync(instanceId, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToEffectiveConfiguration(config));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });
    }
}
