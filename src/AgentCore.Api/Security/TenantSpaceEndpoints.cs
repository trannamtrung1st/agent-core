using AgentCore.Application.Security;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Spaces;

namespace AgentCore.Api.Security;

public static class TenantSpaceEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v2/tenants/{tenantId:guid}/agents/{instanceId:guid}/private-space", (Guid instanceId, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.PrivateAsync(actor.PrincipalId, actor.TenantId, instanceId, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        })).RequireAuthorization();
        app.MapGet("/api/v2/tenants/{tenantId:guid}/agents/{instanceId:guid}/context-options", (Guid instanceId, Guid? spaceId, int? page, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.ExecutionOptionsAsync(actor.PrincipalId, actor.TenantId, instanceId, spaceId, page ?? 0, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        })).RequireAuthorization();
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/spaces").RequireAuthorization();
        group.MapGet("", (int? page, int? pageSize, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.ListAsync(actor.PrincipalId, actor.TenantId, page ?? 0, pageSize ?? 30, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false));
        }));
        group.MapGet("/{spaceId:guid}/access", (Guid spaceId, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.AccessAsync(actor.PrincipalId, actor.TenantId, spaceId, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        group.MapGet("/{spaceId:guid}/memberships/{instanceId:guid}/bindings", (Guid spaceId, Guid instanceId, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.MemberBindingsAsync(actor.PrincipalId, actor.TenantId, spaceId, instanceId, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        group.MapPost("", (SpaceCreateRequest body, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.CreateAsync(actor.PrincipalId, actor.TenantId, body.SpaceId, body.Name, body.Description, body.Instructions,
                body.ExpectedRevision, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false), statusCode: 201);
        }));
        group.MapGet("/{spaceId:guid}", (Guid spaceId, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.ReadAsync(actor.PrincipalId, actor.TenantId, spaceId, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false));
        }));
        group.MapPatch("/{spaceId:guid}", (Guid spaceId, SpaceUpdateRequest body, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.UpdateAsync(actor.PrincipalId, actor.TenantId, spaceId, body.Name, body.Description, body.Instructions,
                body.EnvironmentsEnabled, body.ExpectedRevision, body.ExpectedSpaceRevision, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        group.MapDelete("/{spaceId:guid}", (Guid spaceId, long expectedRevision, long expectedSpaceRevision, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            await service.DeleteAsync(actor.PrincipalId, actor.TenantId, spaceId, expectedRevision, expectedSpaceRevision, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false); return Results.NoContent();
        }));
        group.MapGet("/{spaceId:guid}/memberships", (Guid spaceId, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(new { items = await service.MembersAsync(actor.PrincipalId, actor.TenantId, spaceId, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false) });
        }));
        group.MapPut("/{spaceId:guid}/memberships/{instanceId:guid}", (Guid spaceId, Guid instanceId, SpaceMembershipRequest body, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var bindings = body.AddedBindings.Select(b => new SpaceResourceBinding(Parse(b.Kind), b.ResourceKey, b.AccountRef)).ToArray();
            return Results.Json(await service.SetMemberAsync(actor.PrincipalId, actor.TenantId, spaceId, instanceId, body.Responsibility, body.Instructions,
                body.DisabledInheritedBindingKeys, bindings, body.ExpectedRevision, body.ExpectedSpaceRevision, body.ExpectedMembershipRevision,
                initiator: IdentityHttp.TenantHuman(http), ct: ct).ConfigureAwait(false));
        }));
        group.MapDelete("/{spaceId:guid}/memberships/{instanceId:guid}", (Guid spaceId, Guid instanceId, long expectedRevision, long expectedSpaceRevision,
            long expectedMembershipRevision, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            await service.SetMemberAsync(actor.PrincipalId, actor.TenantId, spaceId, instanceId, "", "", [], [], expectedRevision, expectedSpaceRevision,
                expectedMembershipRevision, remove: true, initiator: IdentityHttp.TenantHuman(http), ct: ct).ConfigureAwait(false); return Results.NoContent();
        }));
    }
    private static SpaceBindingKind Parse(string value) => Enum.TryParse<SpaceBindingKind>(value, false, out var parsed)
        && Enum.IsDefined(parsed) && Enum.GetNames<SpaceBindingKind>().Contains(value) ? parsed : throw new ArgumentException("Unknown resource binding kind.");
}
