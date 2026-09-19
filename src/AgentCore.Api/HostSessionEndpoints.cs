using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Api.Realtime;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api;

public static class HostSessionEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/host/sessions").AddEndpointFilter<OwnerCapabilityFilter>();

        group.MapPost("", async (
            HostCreateSessionRequest? body,
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

                var purpose = HttpMapping.ParseHostPurpose(body.Purpose, body.MaxDurationSeconds, out var maxDuration);
                var policy = HttpMapping.ParseHostPolicy(body.CompletionPolicy);
                var snapshot = await sessions.CreateAsync(
                        body.AgentId,
                        body.AgentVersion,
                        HttpMapping.ParseMode(body.Mode),
                        cancellationToken,
                        purpose,
                        policy,
                        maxDuration,
                        body.SpeechLocale)
                    .ConfigureAwait(false);
                var view = HttpMapping.ToHostView(snapshot, activeResponseId: null);
                http.Response.Headers.Location = $"/api/v2/host/sessions/{view.SessionId}";
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

                return Results.Json(HttpMapping.ToHostView(snapshot, host.ActiveResponseId(sessionId)));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("{sessionId:guid}/lifecycle", async (
            Guid sessionId,
            TransitionLifecycleRequest? body,
            SessionHost host,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (body is null || string.IsNullOrWhiteSpace(body.Target))
                {
                    throw AgentCoreErrors.Validation("target is required.");
                }

                var snapshot = await host.TransitionLifecycleAsync(
                        sessionId,
                        LifecycleTransition.Parse(body.Target),
                        LifecycleTransitionSource.Host,
                        body.Reason,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(HttpMapping.ToHostView(snapshot, host.ActiveResponseId(sessionId)));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });
    }
}
