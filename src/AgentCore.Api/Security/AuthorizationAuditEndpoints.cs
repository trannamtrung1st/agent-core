using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Api.Security;

internal static class AuthorizationAuditEndpoints
{
    // The existing limit parameter remains a bounded page-size hint for incumbent consumers.
    private sealed record Filters(int? Page, int? PageSize, int? Limit, string? Action, string? Decision,
        Guid? ActorPrincipalId, DateTimeOffset? From, DateTimeOffset? Through)
    {
        public AuthorizationAuditQuery Query => new(Page ?? 0, PageSize ?? Limit ?? 30, Action, Decision, ActorPrincipalId, From, Through);
    }
    internal static void Map(WebApplication app)
    {
        app.MapGet("/api/v2/tenants/{tenantId:guid}/audit", (HttpContext http, [AsParameters] Filters filters,
            AuthorizationAuditQueryService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var human = new HumanSessionReference(IdentityHttp.AccountId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"),
                IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
            return Page(await service.ReadTenantAsync(actor.TenantId, human, filters.Query, ct).ConfigureAwait(false));
        })).RequireAuthorization();
        app.MapGet("/api/v2/system/audit", (HttpContext http, [AsParameters] Filters filters,
            IIamStore security, AuthorizationAuditQueryService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Page(await service.ReadSystemAsync(await IdentityHttp.SystemHumanAsync(http, security, ct).ConfigureAwait(false), filters.Query, ct).ConfigureAwait(false))))
            .RequireAuthorization();
    }
    private static IResult Page(AuthorizationAuditPage page) => Results.Json(new { items = page.Items.Select(TenantAccessEndpoints.AuditEntry), page.Total, page.Page, page.PageSize });
}
