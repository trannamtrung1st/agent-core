using AgentCore.Api.Mapping;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using Microsoft.AspNetCore.Mvc;

namespace AgentCore.Api.Security;

public static partial class TenantAgentEndpoints
{
    public sealed record SkillCommand(string Operation, long ExpectedRevision, long ExpectedInstanceRevision, string? Key = null,
        long? ExpectedSkillRevision = null, InstanceSkillInput? Input = null, bool? Enabled = null);
    public sealed record ResourceUpsert(long ExpectedRevision, long ExpectedInstanceRevision, TenantAgentResourceInput Input);
    public sealed record ResourceChoice(long ExpectedRevision, long ExpectedInstanceRevision, long ExpectedResourceRevision, bool? Enabled);
    public sealed record ResourceCopy(long ExpectedRevision, long ExpectedInstanceRevision, string LogicalPath);
    private static void MapChildren(RouteGroupBuilder group)
    {
        group.MapGet("/file-preview/options", (FilePreviewOptions policy) => Results.Json(policy));
        group.MapGet("/agents/{instanceId:guid}/resources/{key}/editor", (Guid instanceId, string key, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            var value = await service.ReadResourceDetailAsync(a.TenantId, instanceId, Human(http), key, ct).ConfigureAwait(false);
            return Results.Json(new { agent = Safe(value.Agent), value.Item, value.SecurityRevision });
        }));
        group.MapGet("/agents/{instanceId:guid}/resources/{key}/office-preview", (Guid instanceId, string key, string? sheet, string? range, HttpContext http,
            TenantAgentReadService service, IOfficeProcessor office, FilePreviewOptions policy, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            http.Response.Headers.XContentTypeOptions = "nosniff";
            try
            {
                return await service.ProjectResourceContentAsync(a.TenantId, instanceId, Human(http), key,
                    item => policy.OfficeLimit(item.LogicalPath, item.MediaType),
                    (source, token) => ValueTask.FromResult(OfficePreviewMapping.Project(Path.GetFileName(source.Resource.LogicalPath),
                        source.Resource.MediaType, source.Bytes, sheet, range, office, policy, token)), ct).ConfigureAwait(false);
            }
            catch (AgentCore.Application.Sessions.AgentCoreException ex) { return OfficePreviewMapping.Problem(ex); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or System.Xml.XmlException)
            { return Results.Problem(statusCode: 400, title: "invalid_office_package", detail: "This Office package could not be interpreted safely. Download the original."); }
        }));
        group.MapGet("/agents/{instanceId:guid}/skills/{key}/editor", (Guid instanceId, string key, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            var value = await service.ReadSkillDetailAsync(a.TenantId, instanceId, Human(http), key, ct).ConfigureAwait(false);
            return Results.Json(new { agent = Safe(value.Agent), item = new { skill = Mapping.AdminHttpMapping.ToInstanceSkill(value.Item.Skill), value.Item.CanManage, value.Item.CanCustomize }, value.SecurityRevision });
        }));
        group.MapGet("/agents/{instanceId:guid}/skills/editor", (Guid instanceId, int? page, int? pageSize, string? search, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            var value = await service.ReadSkillEditorAsync(a.TenantId, instanceId, Human(http), page ?? 0, pageSize ?? 30, search, ct).ConfigureAwait(false);
            return Results.Json(new { agent = Safe(value.Agent), items = value.Items.Select(item => new { skill = Mapping.AdminHttpMapping.ToInstanceSkill(item.Skill), item.CanManage, item.CanCustomize }), value.Total, value.Page, value.PageSize, value.SecurityRevision, value.CanCreate, value.AllReadable });
        }));
        group.MapGet("/agents/{instanceId:guid}/resources/editor", (Guid instanceId, int? page, int? pageSize, string? search, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            var value = await service.ReadResourceEditorAsync(a.TenantId, instanceId, Human(http), page ?? 0, pageSize ?? 30, search, ct).ConfigureAwait(false);
            return Results.Json(new { agent = Safe(value.Agent), value.Items, value.Total, value.Page, value.PageSize, value.SecurityRevision, value.CanCreate, value.AllReadable });
        }));
        group.MapGet("/agents/{instanceId:guid}/skills", (Guid instanceId, int? page, int? pageSize, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ReadSkillsAsync(a.TenantId, instanceId, Human(http), page ?? 0, pageSize ?? 30, ct).ConfigureAwait(false));
        }));
        group.MapPost("/agents/{instanceId:guid}/skills/commands", (Guid instanceId, SkillCommand body, HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.WriteSkillAsync(a.PrincipalId, a.TenantId, instanceId, body.Operation,
                body.ExpectedRevision, body.ExpectedInstanceRevision, body.Key, body.ExpectedSkillRevision, body.Input, body.Enabled, ct, Human(http)).ConfigureAwait(false));
        })).WithMetadata(new RequestSizeLimitAttribute(64 * 1024));
        group.MapGet("/agents/{instanceId:guid}/resources", (Guid instanceId, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ReadResourcesAsync(a.TenantId, instanceId, Human(http), ct).ConfigureAwait(false));
        }));
        group.MapGet("/agents/{instanceId:guid}/resources/{key}/content", (Guid instanceId, string key, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); var file = await service.ReadResourceContentAsync(a.TenantId, instanceId, Human(http), key, ct).ConfigureAwait(false);
            return Results.File(file.Bytes, file.Resource.MediaType, Path.GetFileName(file.Resource.LogicalPath));
        }));
        group.MapPost("/agents/{instanceId:guid}/resources", (Guid instanceId, ResourceUpsert body, HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.UpsertResourceAsync(a.PrincipalId, a.TenantId, instanceId, body.ExpectedRevision,
                body.ExpectedInstanceRevision, body.Input, ct, Human(http)).ConfigureAwait(false));
        })).WithMetadata(new RequestSizeLimitAttribute(12 * 1024 * 1024));
        group.MapPost("/agents/{instanceId:guid}/resources/{key}/copy", (Guid instanceId, string key, ResourceCopy body, HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.CopyResourceAsync(a.PrincipalId, a.TenantId, instanceId, key, body.LogicalPath,
                body.ExpectedRevision, body.ExpectedInstanceRevision, ct, Human(http)).ConfigureAwait(false));
        }));
        group.MapPost("/agents/{instanceId:guid}/resources/{key}/enabled", (Guid instanceId, string key, ResourceChoice body, HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ChangeResourceAsync(a.PrincipalId, a.TenantId, instanceId, key, false, body.Enabled,
                body.ExpectedRevision, body.ExpectedInstanceRevision, body.ExpectedResourceRevision, ct, Human(http)).ConfigureAwait(false));
        }));
        group.MapDelete("/agents/{instanceId:guid}/resources/{key}", (Guid instanceId, string key, long expectedRevision, long expectedInstanceRevision, long expectedResourceRevision,
            HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ChangeResourceAsync(a.PrincipalId, a.TenantId, instanceId, key, true, null,
                expectedRevision, expectedInstanceRevision, expectedResourceRevision, ct, Human(http)).ConfigureAwait(false));
        }));
    }
}
