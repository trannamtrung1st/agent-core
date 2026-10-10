using AgentCore.Api.Http;
using AgentCore.Application.Composer;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api;

public static class ComposerEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v2/agent-instances/{instanceId:guid}/composer", async (Guid instanceId, string category,
            string? search, string? cursor, ComposerReferenceService composer, CancellationToken ct) =>
        {
            try { return Results.Json(await composer.DiscoverAsync(new(instanceId, LocalUserProfile.Id), category, search, cursor, ct), UserMessageContent.Json); }
            catch (AgentCoreException exception) { return ProblemResults.From(exception); }
        }).AddEndpointFilter<OwnerCapabilityFilter>();
    }
}
