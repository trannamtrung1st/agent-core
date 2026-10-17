using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using Microsoft.AspNetCore.Mvc;

namespace AgentCore.Api.Security;

public static class TenantCredentialBindingEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/agents/{instanceId:guid}/credential-bindings").RequireAuthorization();
        group.MapGet("", (Guid instanceId, int? page, int? pageSize, HttpContext http, [FromServices] TenantCredentialBindingService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var human = new TenantSessionInitiator(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
            var result = await service.ListForHumanAsync(actor.TenantId, instanceId, human, page ?? 0, pageSize ?? 30, ct).ConfigureAwait(false);
            return Results.Json(new { items = result.Items.Select(Safe), result.Total, result.Page, result.PageSize, result.AllRead, result.InstanceRevision, result.SecurityRevision });
        }));
        group.MapPost("", (Guid instanceId, TenantBindCredentialRequest body, HttpContext http, [FromServices] TenantCredentialBindingService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.BindAsync(actor.PrincipalId, actor.TenantId, instanceId, body.CredentialId, body.Reference,
                body.ExpectedRevision, body.ExpectedInstanceRevision, body.ExpectedCredentialRevision, ct).ConfigureAwait(false);
            return Results.Json(new { binding = Safe(result.Binding), result.InstanceRevision, result.SecurityRevision }, statusCode: 201);
        }));
        group.MapDelete("/{bindingId:guid}", (Guid instanceId, Guid bindingId, long expectedRevision, long expectedInstanceRevision, long expectedBindingRevision,
            HttpContext http, [FromServices] TenantCredentialBindingService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.UnbindAsync(actor.PrincipalId, actor.TenantId, instanceId, bindingId,
                expectedRevision, expectedInstanceRevision, expectedBindingRevision, ct).ConfigureAwait(false));
        }));
    }
    private static AdminCredentialBindingResponse Safe(AgentCore.Application.Credentials.CredentialBindingView value) =>
        new(value.BindingId, value.CredentialId, value.Reference, value.Revision, AdminCredentialEndpoints.Map(value.Credential));
}
