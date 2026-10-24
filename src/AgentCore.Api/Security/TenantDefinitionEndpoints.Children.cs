using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using Microsoft.AspNetCore.Mvc;

namespace AgentCore.Api.Security;

public static partial class TenantDefinitionEndpoints
{
    public sealed record ResourceBatchRequest(long ExpectedRevision, long ExpectedDraftRevision, IReadOnlyList<TenantDefinitionResourceUpload> Items);
    public sealed record ScenarioRequest(string Title, string Prompt, DefinitionEvaluationRequirementLevel RequirementLevel,
        DefinitionEvaluationCheckType CheckType, string? ToolName, long ExpectedRevision, long ExpectedDraftRevision);
    public sealed record EvaluationRequest(long ExpectedRevision, long ExpectedDraftRevision);
    private static void MapChildren(RouteGroupBuilder group)
    {
        group.MapGet("/definition-drafts/{draftId:guid}/state", (Guid draftId, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ReadDraftStateAsync(a.PrincipalId, a.TenantId, draftId, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false));
        }));
        group.MapGet("/definition-drafts/{draftId:guid}/resources", (Guid draftId, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); var snapshot = await service.ReadDraftStateAsync(a.PrincipalId, a.TenantId, draftId, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false);
            return Results.Json(new { items = snapshot.Resources, snapshot.Draft.Revision, snapshot.ResourceAccess });
        }));
        group.MapPost("/definition-drafts/{draftId:guid}/resources/batch", (Guid draftId, ResourceBatchRequest body, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.BindResourcesAsync(a.PrincipalId, a.TenantId, draftId,
                body.ExpectedRevision, body.ExpectedDraftRevision, body.Items, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false));
        })).WithMetadata(new RequestSizeLimitAttribute(24 * 1024 * 1024));
        group.MapDelete("/definition-drafts/{draftId:guid}/resources/{resourceId:guid}", (Guid draftId, Guid resourceId, long expectedRevision, long expectedDraftRevision, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.RemoveResourceAsync(a.PrincipalId, a.TenantId, draftId, resourceId, expectedRevision, expectedDraftRevision, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false));
        }));
        group.MapGet("/definition-drafts/{draftId:guid}/resources/{resourceId:guid}/content", (Guid draftId, Guid resourceId, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Download(await service.ReadDraftResourceAsync(a.PrincipalId, a.TenantId, draftId, resourceId, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false));
        }));
        group.MapGet("/agent-definitions/{definitionId:guid}/publications/{version:int}/resources", (Guid definitionId, int version, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.PublicationResourceStateAsync(a.PrincipalId, a.TenantId, definitionId, version, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false));
        }));
        group.MapGet("/agent-definitions/{definitionId:guid}/publications/{version:int}/resources/{resourceId:guid}/content", (Guid definitionId, int version, Guid resourceId, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Download(await service.ReadPublicationResourceAsync(a.PrincipalId, a.TenantId, definitionId, version, resourceId, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false));
        }));
        group.MapGet("/definition-drafts/{draftId:guid}/evaluation-scenarios", (Guid draftId, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); var snapshot = await service.ReadDraftStateAsync(a.PrincipalId, a.TenantId, draftId, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false);
            return Results.Json(new { items = snapshot.Scenarios, snapshot.Draft.Revision });
        }));
        group.MapPut("/definition-drafts/{draftId:guid}/evaluation-scenarios/{scenarioId}", (Guid draftId, string scenarioId, ScenarioRequest body, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.SaveScenarioAsync(a.PrincipalId, a.TenantId, draftId,
                body.ExpectedRevision, body.ExpectedDraftRevision, new(draftId, scenarioId, body.Title, body.Prompt, body.RequirementLevel, body.CheckType, body.ToolName, DateTimeOffset.MinValue), ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false));
        }));
        group.MapDelete("/definition-drafts/{draftId:guid}/evaluation-scenarios/{scenarioId}", (Guid draftId, string scenarioId, long expectedRevision, long expectedDraftRevision, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.RemoveScenarioAsync(a.PrincipalId, a.TenantId, draftId, scenarioId, expectedRevision, expectedDraftRevision, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false));
        }));
        group.MapPost("/definition-drafts/{draftId:guid}/evaluation-scenarios/{scenarioId}/run", (Guid draftId, string scenarioId, EvaluationRequest body, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.RunScenarioAsync(a.PrincipalId, a.TenantId, draftId, scenarioId, body.ExpectedRevision, body.ExpectedDraftRevision, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false));
        }));
        group.MapGet("/definition-drafts/{draftId:guid}/evaluation-results", (Guid draftId, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(new { items = (await service.ReadDraftStateAsync(a.PrincipalId, a.TenantId, draftId, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false)).Results });
        }));
    }
    private static IResult Download(TenantDefinitionResourceContent resource) => Results.File(resource.Content, resource.MediaType, Path.GetFileName(resource.LogicalPath));
}
