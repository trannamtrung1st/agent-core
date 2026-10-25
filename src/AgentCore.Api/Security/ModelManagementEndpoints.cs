using AgentCore.Api.Mapping;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Models;

namespace AgentCore.Api.Security;

internal static class ModelManagementEndpoints
{
    internal static void Map(WebApplication app)
    {
        var tenant = app.MapGroup("/api/v2/tenants/{tenantId:guid}").RequireAuthorization();
        tenant.MapGet("/models", (HttpContext http, TenantModelManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Results.Json(ModelManagementMapping.Map(await service.ReadAsync(IdentityHttp.TenantHuman(http), IdentityHttp.TenantContext(http).TenantId, ct)))));
        tenant.MapGet("/model-connections", (HttpContext http, TenantModelManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var view = await service.ReadAsync(IdentityHttp.TenantHuman(http), IdentityHttp.TenantContext(http).TenantId, ct);
            return Results.Json(new { items = view.Connections.Select(ModelManagementMapping.Map), view.SecurityRevision });
        }));
        tenant.MapGet("/model-connections/{id:guid}", (Guid id, HttpContext http, TenantModelManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var view = await service.ReadAsync(IdentityHttp.TenantHuman(http), IdentityHttp.TenantContext(http).TenantId, ct);
            return Results.Json(ModelManagementMapping.Map(view.Connections.SingleOrDefault(c => c.ConnectionId == id) ?? throw new AccessDeniedException("resource_unavailable")));
        }));
        tenant.MapGet("/models/{id:guid}", (Guid id, HttpContext http, TenantModelManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var view = await service.ReadAsync(IdentityHttp.TenantHuman(http), IdentityHttp.TenantContext(http).TenantId, ct);
            return Results.Json(ModelManagementMapping.Map(view.Offerings.SingleOrDefault(o => o.OfferingId == id) ?? throw new AccessDeniedException("resource_unavailable")));
        }));
        foreach (var (path, connection) in new[] { ("/models/{id:guid}/references", false), ("/model-connections/{id:guid}/references", true) })
            tenant.MapGet(path, (Guid id, string? kind, int? page, int? pageSize, HttpContext http, TenantModelManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            {
                var category = kind?.ToLowerInvariant() switch
                {
                    null or "default" => ModelReferenceKind.Default, "definitions" => ModelReferenceKind.Definitions,
                    "instances" => ModelReferenceKind.Instances, "automations" => ModelReferenceKind.Automations,
                    "sessions" => ModelReferenceKind.Sessions, "runs" => ModelReferenceKind.Runs,
                    _ => throw AgentCoreErrors.Validation("Choose a supported model reference category.")
                };
                return Results.Json(await service.ReferencesAsync(IdentityHttp.TenantHuman(http), IdentityHttp.TenantContext(http).TenantId,
                    id, connection, category, page ?? 0, pageSize ?? 20, ct));
            }));
        tenant.MapPost("/model-connections", (ModelConnectionRequest body, HttpContext http, TenantModelManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Results.Json(ModelManagementMapping.Map(await service.SaveConnectionAsync(IdentityHttp.TenantHuman(http), IdentityHttp.TenantContext(http).TenantId,
                null, ModelManagementMapping.Input(body), body.ExpectedRevision, body.ExpectedConnectionRevision, ct)), statusCode: 201)));
        tenant.MapPatch("/model-connections/{id:guid}", (Guid id, ModelConnectionRequest body, HttpContext http, TenantModelManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Results.Json(ModelManagementMapping.Map(await service.SaveConnectionAsync(IdentityHttp.TenantHuman(http), IdentityHttp.TenantContext(http).TenantId,
                id, ModelManagementMapping.Input(body), body.ExpectedRevision, body.ExpectedConnectionRevision, ct)))));
        tenant.MapPost("/model-connections/{id:guid}/test", (Guid id, ModelMetadataTestRequest body, HttpContext http, TenantModelManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Results.Json(ModelManagementMapping.Map(await service.TestAsync(IdentityHttp.TenantHuman(http), IdentityHttp.TenantContext(http).TenantId,
                id, false, body.ExpectedRevision, body.ExpectedConnectionRevision, ct)))));
        tenant.MapPost("/model-connections/{id:guid}/discover", (Guid id, ModelMetadataTestRequest body, HttpContext http, TenantModelManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Results.Json(ModelManagementMapping.Map(await service.TestAsync(IdentityHttp.TenantHuman(http), IdentityHttp.TenantContext(http).TenantId,
                id, true, body.ExpectedRevision, body.ExpectedConnectionRevision, ct)))));
        tenant.MapPost("/models", (ModelOfferingRequest body, HttpContext http, TenantModelManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Results.Json(ModelManagementMapping.Map(await service.SaveOfferingAsync(IdentityHttp.TenantHuman(http), IdentityHttp.TenantContext(http).TenantId,
                null, ModelManagementMapping.Input(body), body.ExpectedRevision, body.ExpectedOfferingRevision, ct)), statusCode: 201)));
        tenant.MapPatch("/models/{id:guid}", (Guid id, ModelOfferingRequest body, HttpContext http, TenantModelManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Results.Json(ModelManagementMapping.Map(await service.SaveOfferingAsync(IdentityHttp.TenantHuman(http), IdentityHttp.TenantContext(http).TenantId,
                id, ModelManagementMapping.Input(body), body.ExpectedRevision, body.ExpectedOfferingRevision, ct)))));
        tenant.MapGet("/models/default", (HttpContext http, TenantModelManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var view = await service.ReadAsync(IdentityHttp.TenantHuman(http), IdentityHttp.TenantContext(http).TenantId, ct);
            return Results.Json(new { settings = ModelManagementMapping.Map(view.Settings), view.SecurityRevision });
        }));
        tenant.MapPut("/models/default", (TenantModelDefaultRequest body, HttpContext http, TenantModelManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Results.Json(ModelManagementMapping.Map(await service.SaveDefaultAsync(IdentityHttp.TenantHuman(http), IdentityHttp.TenantContext(http).TenantId,
                body.OfferingId, body.ReasoningEffort, body.ExpectedRevision, body.ExpectedSettingsRevision, ct)))));
        var system = app.MapGroup("/api/v2/system/model-presets").RequireAuthorization();
        system.MapGet("", (HttpContext http, IIamStore security, SystemModelPresetService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Results.Json((await service.ReadSummariesAsync(await IdentityHttp.SystemHumanAsync(http, security, ct), ct)).Select(ModelManagementMapping.Map))));
        system.MapGet("/{id:guid}", (Guid id, HttpContext http, IIamStore security, SystemModelPresetService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Results.Json(ModelManagementMapping.Map((await service.ReadSummariesAsync(await IdentityHttp.SystemHumanAsync(http, security, ct), ct)).SingleOrDefault(p => p.Preset.PresetId == id)
                ?? throw AgentCoreErrors.NotFound("Model preset was not found.")))));
        system.MapPost("", (ModelPresetRequest body, HttpContext http, IIamStore security, IIdGenerator ids, SystemModelPresetService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Results.Json(ModelManagementMapping.Map(await service.SaveAsync(await IdentityHttp.SystemHumanAsync(http, security, ct),
                Preset(ids.NewId(), body), body.ExpectedRevision, ct)), statusCode: 201)));
        system.MapPatch("/{id:guid}", (Guid id, ModelPresetRequest body, HttpContext http, IIamStore security, SystemModelPresetService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Results.Json(ModelManagementMapping.Map(await service.SaveAsync(await IdentityHttp.SystemHumanAsync(http, security, ct),
                Preset(id, body), body.ExpectedRevision, ct)))));
    }
    private static ModelPreset Preset(Guid id, ModelPresetRequest p) => new(id, ModelManagementMapping.Parse<ModelProviderKind>(p.Kind), p.ExternalModelId,
        p.Label, ModelManagementMapping.Parse<ModelTransport>(p.Transport), ModelManagementMapping.Input(p.Capabilities), p.Enabled, p.Archived, checked(p.ExpectedRevision + 1));
}
