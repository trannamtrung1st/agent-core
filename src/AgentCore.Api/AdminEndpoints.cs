using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Mvc;

namespace AgentCore.Api;

internal static class AdminDefinitionResourceHttp
{
    private const int ReadBufferSize = 64 * 1024;

    internal static async ValueTask<byte[]> ReadContentBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > AgentResourceLimits.MaxItemBytes)
        {
            throw AgentCoreErrors.Validation("Resource content exceeds the per-item size limit.");
        }

        await using var stream = new MemoryStream();
        var buffer = new byte[ReadBufferSize];
        long total = 0;
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > AgentResourceLimits.MaxItemBytes)
            {
                throw AgentCoreErrors.Validation("Resource content exceeds the per-item size limit.");
            }

            stream.Write(buffer, 0, read);
        }

        if (stream.Length == 0)
        {
            throw AgentCoreErrors.Validation("Resource content must not be empty.");
        }

        return stream.ToArray();
    }

    internal static AgentDefinitionResourceKind ParseResourceKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw AgentCoreErrors.Validation("kind is required.");
        }

        if (kind.Contains(',', StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Validation("kind is invalid.");
        }

        foreach (var name in Enum.GetNames<AgentDefinitionResourceKind>())
        {
            if (string.Equals(name, kind, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<AgentDefinitionResourceKind>(name, ignoreCase: false);
            }
        }

        throw AgentCoreErrors.Validation("kind is invalid.");
    }

    internal static Guid? ParseOptionalResourceId(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        if (!Guid.TryParse(resourceId, out var parsed) || parsed == Guid.Empty)
        {
            throw AgentCoreErrors.Validation("resourceId is invalid.");
        }

        return parsed;
    }
}

internal static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/admin").AddEndpointFilter<OwnerCapabilityFilter>();

        group.MapGet("/definitions", async (
            AdminReadService admin,
            CancellationToken cancellationToken) =>
        {
            var items = await admin.ListDefinitionsAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(new AdminDefinitionInventoryResponse(items.Select(AdminHttpMapping.ToDefinitionItem).ToArray()));
        });

        group.MapGet("/instances", async (
            AdminReadService admin,
            CancellationToken cancellationToken) =>
        {
            var items = await admin.ListInstancesAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(new AdminInstanceInventoryResponse(items.Select(AdminHttpMapping.ToInstanceItem).ToArray()));
        });

        group.MapGet("/tools", () =>
        {
            var names = ToolCatalog.AllKnownNames().OrderBy(name => name, StringComparer.Ordinal).ToArray();
            return Results.Json(new AdminToolRegistryResponse(names));
        });

        group.MapGet("/instances/{instanceId:guid}/effective-config", async (
            Guid instanceId,
            AdminReadService admin,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var config = await admin.GetEffectiveConfigurationAsync(instanceId, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToEffectiveConfiguration(config));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("/agent-instances", async (
            AdminCreateAgentInstanceRequest? request,
            IAgentInstanceService instances,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (request is null || string.IsNullOrWhiteSpace(request.DefinitionId))
                {
                    throw AgentCoreErrors.Validation("definitionId is required.");
                }

                if (request.Version < 1)
                {
                    throw AgentCoreErrors.Validation("version must be a positive publication version.");
                }

                var instance = await instances.CreateAsync(request.DefinitionId, request.Version, cancellationToken)
                    .ConfigureAwait(false);
                if (instance.Compatibility)
                {
                    throw AgentCoreErrors.Conflict("Managed instance creation produced a compatibility row.");
                }

                return Results.Json(
                    new AdminAgentInstanceResponse(
                        instance.InstanceId.ToString("D"),
                        instance.DefinitionId,
                        instance.ActiveVersion,
                        instance.Compatibility,
                        instance.Lifecycle.ToString()),
                    statusCode: StatusCodes.Status201Created);
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("/definition-drafts", async (
            AgentDefinitionLifecycleService lifecycle,
            CancellationToken cancellationToken) =>
        {
            var items = await lifecycle.ListDraftsAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(new AdminDefinitionDraftListResponse(items.Select(AdminHttpMapping.ToDraftSummary).ToArray()));
        });

        group.MapGet("/definition-drafts/{draftId:guid}", async (
            Guid draftId,
            AgentDefinitionLifecycleService lifecycle,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var draft = await lifecycle.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToDraft(draft));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("/definition-drafts", async (
            AdminCreateDefinitionDraftRequest request,
            AgentDefinitionLifecycleService lifecycle,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var candidate = AdminDefinitionJson.ReadCandidate(request.Candidate);
                var draft = await lifecycle.CreateDraftAsync(request.DefinitionId, candidate, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToDraft(draft), statusCode: StatusCodes.Status201Created);
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("/definition-drafts/fork", async (
            AdminForkDefinitionDraftRequest request,
            AgentDefinitionLifecycleService lifecycle,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (!Enum.TryParse<DefinitionDraftSourceKind>(request.SourceKind, ignoreCase: true, out var sourceKind))
                {
                    throw AgentCoreErrors.Validation("sourceKind is invalid.");
                }

                var draft = await lifecycle.ForkDraftAsync(
                        request.DefinitionId,
                        request.SourceVersion,
                        sourceKind,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToDraft(draft), statusCode: StatusCodes.Status201Created);
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPut("/definition-drafts/{draftId:guid}", async (
            Guid draftId,
            AdminUpdateDefinitionDraftRequest request,
            AgentDefinitionLifecycleService lifecycle,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var candidate = AdminDefinitionJson.ReadCandidate(request.Candidate);
                var draft = await lifecycle.UpdateDraftAsync(
                        draftId,
                        request.ExpectedRevision,
                        candidate,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToDraft(draft));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("/definition-drafts/{draftId:guid}/publish", async (
            Guid draftId,
            AdminPublishDefinitionDraftRequest request,
            AgentDefinitionLifecycleService lifecycle,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var publication = await lifecycle.PublishDraftAsync(
                        draftId,
                        request.ExpectedRevision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToPublicationSummary(publication));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("/definitions/{definitionId}/publications", async (
            string definitionId,
            AgentDefinitionLifecycleService lifecycle,
            CancellationToken cancellationToken) =>
        {
            var items = await lifecycle.ListPublicationsAsync(definitionId, cancellationToken).ConfigureAwait(false);
            return Results.Json(new AdminDefinitionPublicationListResponse(items.Select(AdminHttpMapping.ToPublicationSummary).ToArray()));
        });

        group.MapPost("/definitions/{definitionId}/publications/{version:int}/deprecate", async (
            string definitionId,
            int version,
            AdminDeprecateDefinitionPublicationRequest request,
            AgentDefinitionLifecycleService lifecycle,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var publication = await lifecycle.DeprecatePublicationAsync(
                        definitionId,
                        version,
                        request.ExpectedMetadataRevision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToPublicationSummary(publication));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("/definition-drafts/{draftId:guid}/resources", async (
            Guid draftId,
            AgentDefinitionResourceService resources,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var items = await resources.ListDraftResourcesAsync(draftId, cancellationToken).ConfigureAwait(false);
                return Results.Json(new AdminDefinitionDraftResourceListResponse(items.Select(AdminHttpMapping.ToDraftResource).ToArray()));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("/definition-drafts/{draftId:guid}/resources/content", async (
            Guid draftId,
            HttpRequest http,
            AgentDefinitionResourceService resources,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var bytes = await AdminDefinitionResourceHttp.ReadContentBodyAsync(http, cancellationToken)
                    .ConfigureAwait(false);
                var mediaType = string.IsNullOrWhiteSpace(http.ContentType) ? "application/octet-stream" : http.ContentType;
                var stored = await resources.StoreDraftContentAsync(draftId, mediaType, bytes, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToStoredContent(stored), statusCode: StatusCodes.Status201Created);
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(AgentResourceLimits.MaxItemBytes + (1024 * 1024)));

        group.MapPut("/definition-drafts/{draftId:guid}/resources", async (
            Guid draftId,
            AdminUpsertDefinitionDraftResourceRequest request,
            AgentDefinitionResourceService resources,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var kind = AdminDefinitionResourceHttp.ParseResourceKind(request.Kind);
                var resourceId = AdminDefinitionResourceHttp.ParseOptionalResourceId(request.ResourceId);
                var resource = await resources.UpsertDraftResourceAsync(
                        draftId,
                        request.ExpectedRevision,
                        resourceId,
                        request.LogicalPath,
                        kind,
                        request.MediaType,
                        request.ContentSha256,
                        request.ByteLength,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToDraftResource(resource));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapDelete("/definition-drafts/{draftId:guid}/resources/{resourceId:guid}", async (
            Guid draftId,
            Guid resourceId,
            long expectedRevision,
            AgentDefinitionResourceService resources,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var removed = await resources.RemoveDraftResourceAsync(
                        draftId,
                        expectedRevision,
                        resourceId,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToDraftResource(removed));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("/definition-drafts/{draftId:guid}/resources/{resourceId:guid}/content", async (
            Guid draftId,
            Guid resourceId,
            AgentDefinitionResourceService resources,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var bytes = await resources.ReadDraftResourceContentAsync(draftId, resourceId, cancellationToken)
                    .ConfigureAwait(false);
                if (bytes is null)
                {
                    throw AgentCoreErrors.NotFound("Draft resource content was not found.");
                }

                return Results.Bytes(bytes, "application/octet-stream");
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("/definitions/{definitionId}/publications/{version:int}/resources", async (
            string definitionId,
            int version,
            AgentDefinitionResourceService resources,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var items = await resources.ListPublicationResourcesAsync(definitionId, version, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(new AdminDefinitionPublicationResourceListResponse(items.Select(AdminHttpMapping.ToPublicationResource).ToArray()));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });
    }
}
