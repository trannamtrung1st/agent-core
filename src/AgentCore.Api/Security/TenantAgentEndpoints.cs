using AgentCore.Application.Ports;
using AgentCore.Api.Mapping;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;

namespace AgentCore.Api.Security;

public static partial class TenantAgentEndpoints
{
    public sealed record CreateRequest(string TemplateId, int Version, long ExpectedRevision, AgentIdentity? Persona = null);
    public sealed record PublicationCreateRequest(Guid DefinitionId, int Version, long ExpectedRevision, AgentIdentity? Persona = null);
    public sealed record LifecycleRequest(AgentInstanceLifecycle Lifecycle, long ExpectedRevision, long ExpectedInstanceRevision);

    private static TenantSessionInitiator Human(HttpContext http)
    {
        var actor = IdentityHttp.TenantContext(http);
        return new(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    }

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}").RequireAuthorization();
        MapConfiguration(group);
        MapDecommission(group);
        group.MapGet("/agents/{instanceId:guid}/access", (Guid instanceId, int? audiencePage, int? executionPage, int? principalPage,
            string? q, string? authorityScope, HttpContext http, TenantAgentAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var human = new TenantSessionInitiator(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
            return Results.Json(await service.ReadAsync(actor.TenantId, instanceId, human, audiencePage ?? 0, executionPage ?? 0, principalPage ?? 0, q, authorityScope ?? "agent", ct).ConfigureAwait(false));
        }));
        group.MapGet("/chat/models", (Guid? instanceId, HttpContext http, TenantChatService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var catalog = await service.ModelsAsync(actor.TenantId, new(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId), instanceId, ct).ConfigureAwait(false);
            return Results.Json(HttpMapping.ToCatalog(catalog));
        }));
        group.MapGet("/chat/agents", (int? page, int? pageSize, string? q, HttpContext http, TenantChatService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var human = new TenantSessionInitiator(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
            return Results.Json(await service.ListAgentsAsync(actor.TenantId, human, page ?? 0, pageSize ?? 30, ct, q).ConfigureAwait(false));
        }));
        group.MapGet("/agent-templates", (HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var templates = await service.ListTemplatesAsync(actor.TenantId, Human(http), ct).ConfigureAwait(false);
            return Results.Json(new { items = templates.Select(t => new { t.Id, t.Version, t.Identity }) });
        }));
        group.MapPost("/agents", (CreateRequest body, HttpContext http, TenantAgentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var agent = await service.CreateAsync(actor.PrincipalId, actor.TenantId, body.TemplateId, body.Version, body.ExpectedRevision, body.Persona, ct, Human(http)).ConfigureAwait(false);
            return Results.Json(Safe(agent), statusCode: 201);
        }));
        group.MapPost("/agents/from-publication", (PublicationCreateRequest body, HttpContext http, TenantAgentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.CreateFromPublicationAsync(actor.PrincipalId, actor.TenantId, body.DefinitionId,
                body.Version, body.ExpectedRevision, body.Persona, ct, Human(http)).ConfigureAwait(false)), statusCode: 201);
        }));
        group.MapGet("/agents", (int? page, int? pageSize, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ListAsync(actor.TenantId, Human(http), page ?? 0, pageSize ?? 30, ct).ConfigureAwait(false);
            return Results.Json(new { items = result.Items.Select(agent => Safe(agent)), result.Total, result.Page, result.PageSize });
        }));
        group.MapGet("/agents/{instanceId:guid}", (Guid instanceId, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.ReadAgentAsync(actor.TenantId, instanceId, Human(http), ct).ConfigureAwait(false)));
        }));
        group.MapPut("/agents/{instanceId:guid}/lifecycle", (Guid instanceId, LifecycleRequest body, HttpContext http, TenantAgentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.SetLifecycleAsync(actor.PrincipalId, actor.TenantId, instanceId,
                body.Lifecycle, body.ExpectedRevision, body.ExpectedInstanceRevision, ct, Human(http)).ConfigureAwait(false)));
        }));
    }
    private static object Safe(TenantAgent agent) => new { agent.TenantId, agent.SystemIdentityId, agent.PrincipalId,
        instance = AdminHttpMapping.ToAgentInstance(agent.Instance) };
}
