using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Api.Mapping;
using AgentCore.Application.Sessions;

namespace AgentCore.Api.Security;

public static class TenantSessionEndpoints
{
    public sealed record CreateRequest(Guid AgentInstanceId, long ExpectedRevision, long ExpectedInstanceRevision, string Mode = "text", string? ModelKey = null, string? ReasoningEffort = null);
    public sealed record Witness(long ExpectedRevision, long ExpectedSessionRevision);
    public sealed record RenameRequest(string Title, long ExpectedRevision, long ExpectedSessionRevision);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/sessions").RequireAuthorization();
        group.MapPatch("/{sessionId:guid}", (Guid sessionId, RenameRequest body, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ChangeAsync(actor.TenantId, Initiator(http, actor), new(sessionId, body.ExpectedSessionRevision, TenantSessionAction.Rename, body.Title), body.ExpectedRevision, ct).ConfigureAwait(false);
            return Results.Json(Safe(result!));
        }));
        foreach (var action in new[] { TenantSessionAction.Archive, TenantSessionAction.Restore, TenantSessionAction.Pause, TenantSessionAction.Resume, TenantSessionAction.Complete, TenantSessionAction.Cancel })
            group.MapPost("/{sessionId:guid}/" + action.ToString().ToLowerInvariant(), (Guid sessionId, Witness body, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            {
                var actor = IdentityHttp.TenantContext(http);
                var result = await service.ChangeAsync(actor.TenantId, Initiator(http, actor), new(sessionId, body.ExpectedSessionRevision, action), body.ExpectedRevision, ct).ConfigureAwait(false);
                return Results.Json(Safe(result!));
            }));
        group.MapDelete("/{sessionId:guid}", (Guid sessionId, long expectedRevision, long expectedSessionRevision, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            await service.ChangeAsync(actor.TenantId, Initiator(http, actor), new(sessionId, expectedSessionRevision, TenantSessionAction.Delete), expectedRevision, ct).ConfigureAwait(false); return Results.NoContent();
        }));
        group.MapPost("", (CreateRequest body, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var initiator = new TenantSessionInitiator(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
            var item = await service.CreateAsync(actor.TenantId, body.AgentInstanceId, initiator, HttpMapping.ParseMode(body.Mode), body.ExpectedRevision, body.ExpectedInstanceRevision, body.ModelKey, body.ReasoningEffort, ct).ConfigureAwait(false);
            return Results.Json(Safe(item), statusCode: 201);
        }));
        group.MapGet("", (int? page, int? pageSize, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); var result = await service.ListAsync(actor.PrincipalId, actor.TenantId, page ?? 0, pageSize ?? 30, ct).ConfigureAwait(false);
            return Results.Json(new { items = result.Items.Select(Safe), result.Total, result.Page, result.PageSize });
        }));
        group.MapGet("/{sessionId:guid}", (Guid sessionId, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(Safe(await service.ReadAsync(actor.PrincipalId, actor.TenantId, sessionId, ct).ConfigureAwait(false)));
        }));
    }
    private static TenantSessionInitiator Initiator(HttpContext http, AuthenticatedTenant actor) =>
        new(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    private static object Safe(TenantSessionMetadata item) => new { item.Owner.TenantId, item.Owner.SessionId, item.Owner.AgentInstanceId,
        item.Owner.ExecutorPrincipalId, item.Owner.CreatedByPrincipalId, item.Title, mode = item.Mode.ToString().ToLowerInvariant(),
        status = item.Status.ToString().ToLowerInvariant(), item.Revision, item.CreatedAt, item.UpdatedAt,
        lifecycleStatus = LifecycleTransition.ToWire(item.LifecycleStatus), item.ArchivedAt };
}
