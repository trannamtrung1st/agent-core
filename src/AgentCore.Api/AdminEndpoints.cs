using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Definitions;

namespace AgentCore.Api;

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
                return Results.Json(AdminHttpMapping.ToDraft(draft));
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
                return Results.Json(AdminHttpMapping.ToDraft(draft));
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
    }
}
