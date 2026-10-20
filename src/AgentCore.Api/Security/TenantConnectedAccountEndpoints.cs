using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Api.Security;

public static class TenantConnectedAccountEndpoints
{
    private static HumanSessionReference Human(HttpContext http)
    {
        var actor = IdentityHttp.TenantContext(http);
        return new(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    }
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/agents/{instanceId:guid}/connected-accounts").RequireAuthorization();
        group.MapGet("", (Guid instanceId, int? page, int? pageSize, HttpContext http, TenantConnectedAccountService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.ListAsync(actor.TenantId, instanceId, Human(http), page ?? 0, pageSize ?? 20, ct).ConfigureAwait(false));
        }));
        group.MapGet("/{accountRef}/email/messages", (Guid instanceId, string accountRef, string? query, int? limit, HttpContext http, TenantConnectedAccountService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ReadAsync(actor.TenantId, instanceId, accountRef, Human(http), new(ConnectedAccountReadKind.EmailSearch, Query: query, Limit: limit ?? 20), ct).ConfigureAwait(false);
            return Results.Json(new { untrusted = true, mailbox = result.Mailbox });
        }));
        group.MapGet("/{accountRef}/email/message", (Guid instanceId, string accountRef, string messageId, HttpContext http, TenantConnectedAccountService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ReadAsync(actor.TenantId, instanceId, accountRef, Human(http), new(ConnectedAccountReadKind.EmailMessage, Id: messageId), ct).ConfigureAwait(false);
            return Results.Json(new { untrusted = true, message = result.Message });
        }));
        group.MapGet("/{accountRef}/drive/files", (Guid instanceId, string accountRef, string? folderId, string? query, string? cursor, int? limit, HttpContext http, TenantConnectedAccountService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ReadAsync(actor.TenantId, instanceId, accountRef, Human(http), new(ConnectedAccountReadKind.DriveList,
                Query: query, Folder: folderId, Cursor: cursor, Limit: limit ?? 20), ct).ConfigureAwait(false);
            return Results.Json(new { untrusted = true, page = result.Files });
        }));
        group.MapGet("/{accountRef}/drive/files/{fileId}", (Guid instanceId, string accountRef, string fileId, HttpContext http, TenantConnectedAccountService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ReadAsync(actor.TenantId, instanceId, accountRef, Human(http), new(ConnectedAccountReadKind.DriveFile, Id: fileId), ct).ConfigureAwait(false);
            return Results.Json(new { untrusted = true, file = result.File });
        }));
        group.MapGet("/{accountRef}/drive/files/{fileId}/content", (Guid instanceId, string accountRef, string fileId, string expectedVersion, int? maxBytes, HttpContext http, TenantConnectedAccountService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ReadAsync(actor.TenantId, instanceId, accountRef, Human(http), new(ConnectedAccountReadKind.DriveContent,
                Id: fileId, ExpectedVersion: expectedVersion, MaxBytes: maxBytes ?? 50 * 1024 * 1024), ct).ConfigureAwait(false);
            return Results.File(result.Bytes!, "application/octet-stream", AgentCore.Application.Drive.DriveService.Name(result.File!.Name.Replace('/', '_').Replace('\\', '_')));
        }));
    }
}
