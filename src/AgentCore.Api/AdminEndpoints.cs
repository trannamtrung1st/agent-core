using System.Text.Json;
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

                return Results.Json(AdminHttpMapping.ToAgentInstance(instance), statusCode: StatusCodes.Status201Created);
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPatch("/agent-instances/{instanceId:guid}/persona", async (
            Guid instanceId,
            HttpRequest http,
            IAgentInstanceService instances,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var parsed = await AdminAgentInstancePersonaHttp.ReadStrictPersonaUpdateAsync(http, cancellationToken)
                    .ConfigureAwait(false);
                var instance = await instances.UpdatePersonaAsync(
                        instanceId,
                        parsed.Persona,
                        parsed.ExpectedRevision,
                        parsed.ExpectedPersonaRevision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToAgentInstance(instance));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
            catch (JsonException ex)
            {
                return ProblemResults.From(AgentCoreErrors.Validation(ex.Message));
            }
        });

        group.MapPatch("/agent-instances/{instanceId:guid}/lifecycle", async (
            Guid instanceId,
            AdminUpdateAgentInstanceLifecycleRequest? request,
            IAgentInstanceService instances,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (request is null || string.IsNullOrWhiteSpace(request.Lifecycle))
                {
                    throw AgentCoreErrors.Validation("lifecycle is required.");
                }

                if (request.ExpectedRevision < 1)
                {
                    throw AgentCoreErrors.Validation("expectedRevision must be positive.");
                }

                if (!Enum.TryParse<AgentInstanceLifecycle>(request.Lifecycle, ignoreCase: true, out var lifecycle))
                {
                    throw AgentCoreErrors.Validation("lifecycle is invalid.");
                }

                var instance = await instances.SetLifecycleAsync(
                        instanceId,
                        lifecycle,
                        request.ExpectedRevision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToAgentInstance(instance));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPatch("/agent-instances/{instanceId:guid}/active-version", async (
            Guid instanceId,
            AdminReassociateAgentInstanceVersionRequest? request,
            IAgentInstanceService instances,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (request is null)
                {
                    throw AgentCoreErrors.Validation("request body is required.");
                }

                if (request.ExpectedRevision < 1 || request.Version < 1)
                {
                    throw AgentCoreErrors.Validation("expectedRevision and version must be positive.");
                }

                var instance = await instances.UpgradeAsync(
                        instanceId,
                        request.Version,
                        request.ExpectedRevision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToAgentInstance(instance));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("/agent-instances/{instanceId:guid}/learned-memory", async (
            Guid instanceId,
            string scope,
            Guid? sessionId,
            AdminMemoryService memory,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var parsed = AdminMemoryHttp.ParseScope(scope);
                var result = await memory.ListAsync(instanceId, parsed, sessionId, cancellationToken).ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToLearnedMemoryList(result));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapDelete("/agent-instances/{instanceId:guid}/learned-memory/{memoryId:guid}", async (
            Guid instanceId,
            Guid memoryId,
            string scope,
            Guid? sessionId,
            bool confirm,
            AdminMemoryService memory,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (!confirm)
                {
                    throw AgentCoreErrors.Validation("confirm=true is required for destructive memory operations.");
                }

                var parsed = AdminMemoryHttp.ParseScope(scope);
                await memory.DeleteAsync(instanceId, parsed, memoryId, sessionId, cancellationToken).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("/agent-instances/{instanceId:guid}/automation/registrations", async (
            Guid instanceId,
            AdminAutomationService automation,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var items = await automation.ListRegistrationsAsync(instanceId, cancellationToken).ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToAutomationRegistrations(items));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("/agent-instances/{instanceId:guid}/automation/registrations/{registrationId:guid}/cancel", async (
            Guid instanceId,
            Guid registrationId,
            AdminCancelAutomationRegistrationRequest? request,
            AdminAutomationService automation,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (request is null)
                {
                    throw AgentCoreErrors.Validation("request body is required.");
                }

                if (!request.Confirm)
                {
                    throw AgentCoreErrors.Validation("confirm must be true for destructive automation operations.");
                }

                if (request.ExpectedRevision < 1)
                {
                    throw AgentCoreErrors.Validation("expectedRevision must be positive.");
                }

                var cancelled = await automation
                    .CancelRegistrationAsync(instanceId, registrationId, request.ExpectedRevision, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToAutomationRegistration(cancelled));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("/agent-instances/{instanceId:guid}/learned-memory/reset", async (
            Guid instanceId,
            AdminLearnedMemoryResetRequest? request,
            AdminMemoryService memory,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (request is null || string.IsNullOrWhiteSpace(request.Scope))
                {
                    throw AgentCoreErrors.Validation("scope is required.");
                }

                if (!request.Confirm)
                {
                    throw AgentCoreErrors.Validation("confirm must be true for destructive memory operations.");
                }

                var parsed = AdminMemoryHttp.ParseScope(request.Scope);
                Guid? sessionId = null;
                if (!string.IsNullOrWhiteSpace(request.SessionId))
                {
                    if (!Guid.TryParse(request.SessionId, out var parsedSession) || parsedSession == Guid.Empty)
                    {
                        throw AgentCoreErrors.Validation("sessionId is invalid.");
                    }

                    sessionId = parsedSession;
                }

                var result = await memory.ResetScopeAsync(instanceId, parsed, sessionId, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToLearnedMemoryReset(result));
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

        group.MapPost("/definition-drafts/{draftId:guid}/validate", async (
            Guid draftId,
            AgentDefinitionDraftValidationService validation,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await validation.ValidateDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToValidation(result));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("/definition-drafts/{draftId:guid}/diff", async (
            Guid draftId,
            AgentDefinitionDraftDiffService diff,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await diff.GetDraftDiffAsync(draftId, cancellationToken).ConfigureAwait(false);
                return Results.Json(AdminHttpMapping.ToDiff(result));
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
