using AgentCore.Application.Admin;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Api.Security;

public static partial class TenantDefinitionEndpoints
{
    private static void MapAuthoring(RouteGroupBuilder group)
    {
        group.MapGet("/agent-definitions/{definitionId:guid}", (Guid definitionId, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.SummaryAsync(a.PrincipalId, a.TenantId, definitionId, ct).ConfigureAwait(false));
        }));
        group.MapGet("/agent-definitions/{definitionId:guid}/access", (Guid definitionId, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.AccessAsync(a.PrincipalId, a.TenantId, definitionId, ct).ConfigureAwait(false));
        }));
        group.MapGet("/definition-authoring-options", (Guid? definitionId, HttpContext http, TenantDefinitionService service,
            IamAuthorizer authorizer, AdminAuthoringOptionsService options, IToolConfigurationGate gate, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            if (definitionId is { } id) _ = await service.AccessAsync(a.PrincipalId, a.TenantId, id, ct).ConfigureAwait(false);
            else await authorizer.RequireAsync(a.PrincipalId, "agent-definitions.create", AccessScope.ForTenant(a.TenantId), ct).ConfigureAwait(false);
            return Results.Json(new { authoring = options.Get(), limits = AgentCore.Domain.Definitions.ExecutionBudgetCeilings.Default,
                capabilities = ToolRegistry.DefinitionAuthorizable.OrderBy(d => d.Name, StringComparer.Ordinal).Select(d => new { d.Name, d.Category, d.Summary, d.Tags, d.Discoverable,
                    d.DefaultProjectionClass, configured = d.OfferRule != ToolOfferRule.ConfigurationWhenRoleAllows || gate.IsConfigured(d.Name) }) });
        }));
    }
}
