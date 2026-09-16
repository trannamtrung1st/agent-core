using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Sessions;
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
