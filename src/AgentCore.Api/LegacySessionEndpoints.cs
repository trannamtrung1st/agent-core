using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Api.Realtime;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;

namespace AgentCore.Api;

public static class LegacySessionEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sessions").AddEndpointFilter<OwnerCapabilityFilter>();

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
                var location = $"/api/v1/sessions/{view.SessionId}";
                http.Response.Headers.Location = location;
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
                return Results.Json(HttpMapping.ToView(snapshot, host.ActiveResponseId(sessionId)));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("{sessionId:guid}/messages", async (
            Guid sessionId,
            SessionManager sessions,
            long after,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var pageLimit = limit ?? 50;
                var items = await sessions.ReadHistoryAsync(sessionId, after, pageLimit, cancellationToken)
                    .ConfigureAwait(false);
                var next = items.Count == 0 ? after : items[^1].Sequence;
                return Results.Json(new HistoryPageResponse(
                    items.Select(HttpMapping.ToHistoryItem).ToArray(),
                    next,
                    HasMore: items.Count == pageLimit));
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
                await host.TerminateAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });
    }
}
