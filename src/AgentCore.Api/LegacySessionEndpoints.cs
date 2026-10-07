using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api;

public static class LegacySessionEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sessions").AddEndpointFilter<OwnerCapabilityFilter>();

        group.MapGet("{sessionId:guid}", async (
            Guid sessionId,
            SessionManager sessions,
            SessionHost host,
            IModelCatalog catalog,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = host.LiveSnapshot(sessionId)
                    ?? await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return Results.Json(HttpMapping.ToView(snapshot, host.ActiveResponseId(sessionId), catalog));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("{sessionId:guid}/messages", async (
            Guid sessionId,
            SessionManager sessions,
            long? after,
            long? before,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var pageLimit = limit ?? 50;
                var page = await sessions.ReadHistoryPageAsync(sessionId, after, before, pageLimit, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(new HistoryPageResponse(
                    page.Items.Select(HttpMapping.ToHistoryItem).ToArray(),
                    page.NextAfter,
                    page.HasMore,
                    page.HasOlder,
                    page.NextBefore));
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
