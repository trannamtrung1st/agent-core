using AgentCore.Application.Admin;
using AgentCore.Application.Security;
using Microsoft.AspNetCore.Mvc;

namespace AgentCore.Api.Security;

public static partial class TenantDefinitionEndpoints
{
    public sealed record DeleteRequest(AdminDefinitionDeleteWitness Witness, long ExpectedRevision);
    private static void MapDeletion(RouteGroupBuilder group)
    {
        group.MapDelete("/definition-drafts/{draftId:guid}", (Guid draftId, long expectedRevision, long expectedDraftRevision,
            HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            await service.DeleteDraftAsync(a.PrincipalId, a.TenantId, draftId, expectedRevision, expectedDraftRevision, ct).ConfigureAwait(false);
            return Results.NoContent();
        }));
        group.MapDelete("/agent-definitions/{definitionId:guid}", (Guid definitionId, [FromBody] DeleteRequest body,
            HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            await service.DeleteAsync(a.PrincipalId, a.TenantId, definitionId, body.Witness, body.ExpectedRevision, ct).ConfigureAwait(false);
            return Results.NoContent();
        }));
    }
}
