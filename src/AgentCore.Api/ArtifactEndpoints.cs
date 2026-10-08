using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Sessions;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;

namespace AgentCore.Api;

public static class ArtifactEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/sessions/{sessionId:guid}/artifacts")
            .AddEndpointFilter<OwnerCapabilityFilter>()
            .DisableAntiforgery();

        group.MapGet("", async (
            Guid sessionId,
            SessionManager sessions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var records = await sessions.ListArtifactsAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return Results.Json(records.Select(HttpMapping.ToArtifact).ToArray());
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("page", async (Guid sessionId, Guid? before, int? limit, Guid? agentRunId, SessionManager sessions,
            IAgentRunStore runs, IArtifactStore artifacts, ILocalUserProfileService profiles, CancellationToken ct) =>
        {
            try
            {
                var session = await BackgroundSessionEndpoints.RequireSession(sessions, profiles, sessionId, ct);
                if (agentRunId is { } id && (await runs.GetAsync(new(session.AgentInstanceId, session.ProfileId!.Value), id, ct))?.SessionId != sessionId)
                    throw AgentCoreErrors.NotFound("AgentRun was not found.");
                var page = await artifacts.ListPageAsync(sessionId, before, limit ?? 20, ct, agentRunId).ConfigureAwait(false);
                return Results.Json(new ArtifactPageResponse(page.Items.Select(HttpMapping.ToArtifact).ToArray(), page.NextCursor?.ToString("D"), page.HasMore));
            }
            catch (AgentCoreException ex) { return ProblemResults.From(ex); }
        });

        group.MapGet("{artifactId:guid}", async (
            Guid sessionId,
            Guid artifactId,
            SessionManager sessions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var record = await sessions.GetArtifactAsync(sessionId, artifactId, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(HttpMapping.ToArtifact(record));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("{artifactId:guid}/content", async (
            Guid sessionId,
            Guid artifactId,
            SessionManager sessions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var record = await sessions.GetArtifactAsync(sessionId, artifactId, cancellationToken)
                    .ConfigureAwait(false);
                var stream = await sessions.OpenArtifactContentAsync(sessionId, artifactId, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Stream(stream, record.ContentType, record.DisplayName);
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });
    }
}
