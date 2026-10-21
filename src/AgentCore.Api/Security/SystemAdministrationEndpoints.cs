using System.Text.Json.Serialization;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Api.Security;

internal static class SystemAdministrationEndpoints
{
    private sealed record Filters(int? Page, int? PageSize, string? Q, IdentityStatus? Status)
    { public SystemCollectionQuery Query => new(Page ?? 0, PageSize ?? 30, Q, Status); }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record CreateTenantRequest(string Name, string Slug);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record AccountStatusRequest(string Status, string ExpectedVersion);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record TenantStatusRequest(string Status, long ExpectedRevision);
    internal static void Map(WebApplication app)
    {
        var system = app.MapGroup("/api/v2/system").RequireAuthorization();
        system.MapGet("/accounts", (HttpContext http, [AsParameters] Filters filters, IIamStore security, SystemAdministrationService service, CancellationToken ct) =>
            IdentityHttp.Respond(async () => Results.Json(await service.AccountsAsync(await IdentityHttp.SystemHumanAsync(http, security, ct), filters.Query, ct))));
        system.MapGet("/tenants", (HttpContext http, [AsParameters] Filters filters, IIamStore security, SystemAdministrationService service, CancellationToken ct) =>
            IdentityHttp.Respond(async () => Results.Json(await service.TenantsAsync(await IdentityHttp.SystemHumanAsync(http, security, ct), filters.Query, ct))));
        system.MapPost("/tenants", (CreateTenantRequest body, HttpContext http, IIamStore security, SystemAdministrationService service, CancellationToken ct) =>
            IdentityHttp.Respond(async () => Results.Json(await service.CreateTenantAsync(await IdentityHttp.SystemHumanAsync(http, security, ct), body.Name, body.Slug, ct), statusCode: 201)));
        system.MapPut("/accounts/{accountId:guid}/status", (Guid accountId, AccountStatusRequest body, HttpContext http, IIamStore security, SystemAdministrationService service, CancellationToken ct) =>
            IdentityHttp.Respond(async () => { await service.SetAccountStatusAsync(await IdentityHttp.SystemHumanAsync(http, security, ct), accountId, body.ExpectedVersion, SystemAdministrationRules.ParseStatus(body.Status), ct); return Results.NoContent(); }));
        system.MapPut("/tenants/{tenantId:guid}/status", (Guid tenantId, TenantStatusRequest body, HttpContext http, IIamStore security, SystemAdministrationService service, CancellationToken ct) =>
            IdentityHttp.Respond(async () => { await service.SetTenantStatusAsync(await IdentityHttp.SystemHumanAsync(http, security, ct), tenantId, body.ExpectedRevision, SystemAdministrationRules.ParseStatus(body.Status), ct); return Results.NoContent(); }));
    }
}
