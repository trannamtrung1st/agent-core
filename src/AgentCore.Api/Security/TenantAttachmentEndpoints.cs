using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc;

namespace AgentCore.Api.Security;

public static class TenantAttachmentEndpoints
{
    public sealed record MaterializeRequest(long ExpectedRevision);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/sessions/{sessionId:guid}/attachments").RequireAuthorization();
        group.MapGet("/{attachmentId:guid}/materialize", (Guid sessionId, Guid attachmentId, HttpContext http, TenantAttachmentMaterializationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.InspectAsync(actor.TenantId, sessionId, attachmentId, Human(http, actor), ct).ConfigureAwait(false));
        }));
        group.MapPost("/{attachmentId:guid}/materialize", (Guid sessionId, Guid attachmentId, MaterializeRequest body, HttpContext http, TenantAttachmentMaterializationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.MaterializeAsync(actor.TenantId, sessionId, attachmentId, Human(http, actor), body.ExpectedRevision, ct).ConfigureAwait(false);
            return Results.Json(result, statusCode: StatusCodes.Status201Created);
        }));
        group.MapGet("", (Guid sessionId, Guid? before, int? limit, HttpContext http, TenantAttachmentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); var page = await service.ListAsync(actor.TenantId, sessionId, Human(http, actor), before, limit ?? 20, ct).ConfigureAwait(false);
            return Results.Json(new { items = page.Items.Select(Safe), page.NextCursor, page.HasMore });
        }));
        group.MapGet("/{attachmentId:guid}", (Guid sessionId, Guid attachmentId, HttpContext http, TenantAttachmentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(Safe(await service.ReadAsync(actor.TenantId, sessionId, attachmentId, Human(http, actor), ct).ConfigureAwait(false)));
        }));
        group.MapGet("/{attachmentId:guid}/content", (Guid sessionId, Guid attachmentId, HttpContext http, TenantAttachmentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); var content = await service.ReadContentAsync(actor.TenantId, sessionId, attachmentId, Human(http, actor), ct).ConfigureAwait(false);
            http.Response.Headers.XContentTypeOptions = "nosniff"; return Results.File(content.Bytes, content.Attachment.ContentType, content.Attachment.DisplayName);
        }));
        group.MapGet("/{attachmentId:guid}/office-preview", (Guid sessionId, Guid attachmentId, string? sheet, string? range, HttpContext http,
            TenantAttachmentService service, IOfficeProcessor office, FilePreviewOptions policy, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); http.Response.Headers.XContentTypeOptions = "nosniff";
            try { return await service.ProjectAsync(actor.TenantId, sessionId, attachmentId, Human(http, actor), item => policy.OfficeLimit(item.DisplayName, item.ContentType),
                (source, token) => ValueTask.FromResult(OfficePreviewEndpoints.Project(source.Attachment.DisplayName, source.Attachment.ContentType, source.Bytes, sheet, range, office, policy, token)), ct).ConfigureAwait(false); }
            catch (AgentCore.Application.Sessions.AgentCoreException error) { return OfficePreviewEndpoints.Problem(error); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or System.Xml.XmlException)
            { return Results.Problem(statusCode: 400, title: "invalid_office_package", detail: "This Office package could not be interpreted safely. Download the original."); }
        }));
        group.MapPost("", (Guid sessionId, Guid uploadId, long expectedRevision, long expectedSessionRevision, HttpContext http, TenantAttachmentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            await service.AuthorizeUploadAsync(actor.TenantId, sessionId, Human(http, actor), ct).ConfigureAwait(false);
            if (!http.Request.HasFormContentType) throw AgentCore.Application.Sessions.AgentCoreErrors.Validation("Multipart form upload is required.");
            var form = await http.Request.ReadFormAsync(ct).ConfigureAwait(false);
            if (form.Files.Count != 1 || form.Count != 0) throw AgentCore.Application.Sessions.AgentCoreErrors.Validation("Upload exactly one file without extra fields.");
            var file = form.Files.GetFile("file") ?? throw AgentCore.Application.Sessions.AgentCoreErrors.Validation("file is required.");
            await using var stream = file.OpenReadStream();
            var id = await service.UploadAsync(actor.TenantId, sessionId, Human(http, actor), uploadId, expectedRevision, expectedSessionRevision, file.FileName,
                file.ContentType ?? "application/octet-stream", stream, ct).ConfigureAwait(false);
            return Results.Json(new { attachmentId = id }, statusCode: StatusCodes.Status201Created);
        })).WithMetadata(new RequestSizeLimitAttribute(AttachmentLimits.MaxBytesEach + 1024 * 1024));
        group.MapDelete("/{attachmentId:guid}", (Guid sessionId, Guid attachmentId, Guid requestId, long expectedRevision, long expectedSessionRevision, HttpContext http, TenantAttachmentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); await service.AbortAsync(actor.TenantId, sessionId, attachmentId, Human(http, actor), requestId, expectedRevision, expectedSessionRevision, ct).ConfigureAwait(false);
            return Results.NoContent();
        }));
    }
    private static HumanSessionReference Human(HttpContext http, AuthenticatedTenant actor) => new(actor.AccountId,
        IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    private static object Safe(AttachmentRecord item) => AgentCore.Api.Mapping.HttpMapping.ToAttachment(item);
}
