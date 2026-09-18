using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Api.Realtime;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using Microsoft.Extensions.Options;

namespace AgentCore.Api;

public static class SessionCatalogEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/local/owner-capability", async (
            HttpContext http,
            IOwnerCapabilityService capabilities,
            IOptions<HostingOptions> hosting,
            CancellationToken cancellationToken) =>
        {
            if (!TrustedLocalCaller.IsTrustedLocal(http, hosting.Value.TrustPublishedPortGateway))
            {
                return ProblemResults.From(AgentCoreErrors.Forbidden("Owner capability can only be issued on the local host."));
            }

            var issued = await capabilities.IssueAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(new OwnerCapabilityResponse(
                issued.Token,
                HttpMapping.Format(issued.IssuedAt)));
        });

        var group = app.MapGroup("/api/v2/sessions").AddEndpointFilter<OwnerCapabilityFilter>();

        group.MapGet("", async (
            SessionManager sessions,
            string? cursor,
            int? limit,
            bool? includeArchived,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await sessions.ListCatalogAsync(
                        cursor,
                        limit ?? 50,
                        includeArchived == true,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(new SessionCatalogPageResponse(
                    page.Items.Select(HttpMapping.ToCatalogItem).ToArray(),
                    page.NextCursor,
                    page.HasMore));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("", async (
            CreateSessionRequest? body,
            SessionManager sessions,
            SessionHost host,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (!host.Admitting)
                {
                    throw AgentCoreErrors.ShuttingDown();
                }

                if (body is null || string.IsNullOrWhiteSpace(body.AgentId))
                {
                    throw AgentCoreErrors.Validation("agentId is required.");
                }

                var snapshot = await sessions.CreateAsync(
                        body.AgentId,
                        body.AgentVersion,
                        HttpMapping.ParseMode(body.Mode),
                        cancellationToken)
                    .ConfigureAwait(false);
                var view = HttpMapping.ToView(snapshot, activeResponseId: null);
                http.Response.Headers.Location = $"/api/v2/sessions/{view.SessionId}";
                return Results.Json(view, statusCode: StatusCodes.Status201Created);
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("{sessionId:guid}", async (
            Guid sessionId,
            SessionManager sessions,
            SessionHost host,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = host.LiveSnapshot(sessionId)
                    ?? await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (snapshot.DurablyDeletedAt is not null)
                {
                    throw AgentCoreErrors.NotFound("Session was not found.");
                }

                return Results.Json(HttpMapping.ToView(snapshot, host.ActiveResponseId(sessionId)));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("{sessionId:guid}/rename", async (
            Guid sessionId,
            RenameSessionRequest? body,
            SessionHost host,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (body is null)
                {
                    throw AgentCoreErrors.Validation("title is required.");
                }

                var snapshot = await host.RenameAsync(sessionId, body.Title, cancellationToken).ConfigureAwait(false);
                return Results.Json(HttpMapping.ToCatalogItem(snapshot));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("{sessionId:guid}/archive", async (
            Guid sessionId,
            SessionManager sessions,
            SessionHost host,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await host.CancelLiveRuntimeAsync(sessionId, cancellationToken).ConfigureAwait(false);
                var snapshot = await sessions.ArchiveAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return Results.Json(HttpMapping.ToCatalogItem(snapshot));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("{sessionId:guid}/deactivate", async (
            Guid sessionId,
            SessionManager sessions,
            SessionHost host,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await host.DeactivateAsync(sessionId, cancellationToken).ConfigureAwait(false);
                var snapshot = await sessions.DeactivateAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return Results.Json(HttpMapping.ToCatalogItem(snapshot));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("{sessionId:guid}/unarchive", async (
            Guid sessionId,
            SessionManager sessions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = await sessions.UnarchiveAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return Results.Json(HttpMapping.ToCatalogItem(snapshot));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("{sessionId:guid}/reopen", async (
            Guid sessionId,
            SessionManager sessions,
            SessionHost host,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (host.HasActiveLiveConnection(sessionId))
                {
                    throw AgentCoreErrors.SessionInUse();
                }

                var snapshot = await sessions.ReopenAsync(sessionId, cancellationToken).ConfigureAwait(false);
                await host.ApplyReopenedSnapshotToLiveAsync(sessionId, snapshot, cancellationToken).ConfigureAwait(false);
                return Results.Json(HttpMapping.ToCatalogItem(snapshot));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("{sessionId:guid}/knowledge/{identity}", async (
            Guid sessionId,
            string identity,
            SessionManager sessions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var document = await sessions.RetrieveKnowledgeAsync(sessionId, identity, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(HttpMapping.ToKnowledge(document));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapDelete("{sessionId:guid}", async (
            Guid sessionId,
            SessionHost host,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await host.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });
    }
}
