using AgentCore.Application.Security;

namespace AgentCore.Api.Security;

public static partial class TenantDefinitionEndpoints
{
    public sealed record TemplateForkRequest(string TemplateId, int Version, string? Name, long ExpectedRevision);
    private static void MapTemplates(RouteGroupBuilder group)
    {
        group.MapGet("/definition-templates", (HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.TemplateCatalogAsync(a.PrincipalId, a.TenantId, ct).ConfigureAwait(false));
        }));
        group.MapPost("/agent-definitions/from-template", (TemplateForkRequest body, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.ForkTemplateAsync(a.PrincipalId, a.TenantId, body.TemplateId, body.Version, body.Name, body.ExpectedRevision, ct).ConfigureAwait(false), statusCode: 201);
        }));
    }
}
