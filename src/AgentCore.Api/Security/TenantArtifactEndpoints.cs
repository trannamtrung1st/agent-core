using AgentCore.Api.Mapping;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Admin;

namespace AgentCore.Api.Security;

public static class TenantArtifactEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/sessions/{sessionId:guid}/artifacts").RequireAuthorization();
        group.MapGet("", (Guid sessionId, Guid? before, int? limit, Guid? agentRunId, HttpContext http, TenantArtifactService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var page = await service.ListAsync(actor.TenantId, sessionId, Human(http, actor), before, limit ?? 20, agentRunId, ct).ConfigureAwait(false);
            return Results.Json(new { items = page.Items.Select(Safe), page.NextCursor, page.HasMore });
        }));
        group.MapGet("/{artifactId:guid}", (Guid sessionId, Guid artifactId, HttpContext http, TenantArtifactService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(Safe(await service.ReadAsync(actor.TenantId, sessionId, artifactId, Human(http, actor), ct).ConfigureAwait(false)));
        }));
        group.MapGet("/{artifactId:guid}/content", (Guid sessionId, Guid artifactId, HttpContext http, TenantArtifactService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); var content = await service.ReadContentAsync(actor.TenantId, sessionId, artifactId, Human(http, actor), ct).ConfigureAwait(false);
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(content.Bytes, content.Artifact.ContentType, content.Artifact.DisplayName);
        }));
        group.MapGet("/{artifactId:guid}/office-preview", (Guid sessionId, Guid artifactId, string? sheet, string? range, HttpContext http,
            TenantArtifactService service, IOfficeProcessor office, FilePreviewOptions policy, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            http.Response.Headers.XContentTypeOptions = "nosniff";
            try
            {
                return await service.ProjectContentAsync(actor.TenantId, sessionId, artifactId, Human(http, actor),
                    item => policy.OfficeLimit(item.DisplayName, item.ContentType),
                    (source, token) => ValueTask.FromResult(OfficePreviewMapping.Project(source.Artifact.DisplayName,
                        source.Artifact.ContentType, source.Bytes, sheet, range, office, policy, token)), ct).ConfigureAwait(false);
            }
            catch (AgentCore.Application.Sessions.AgentCoreException ex) { return OfficePreviewMapping.Problem(ex); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or System.Xml.XmlException)
            { return Results.Problem(statusCode: 400, title: "invalid_office_package", detail: "This Office package could not be interpreted safely. Download the original."); }
        }));
    }
    private static HumanSessionReference Human(HttpContext http, AuthenticatedTenant actor) => new(actor.AccountId,
        IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    private static object Safe(ArtifactRecord item) => new { item.ArtifactId, item.SessionId, item.DisplayName, item.ContentType, item.ByteSize,
        item.Sha256Hex, item.CreatedAt, item.AgentRunId };
}
