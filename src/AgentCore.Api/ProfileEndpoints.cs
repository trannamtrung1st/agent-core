using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api;

public static class ProfileEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/profile").AddEndpointFilter<OwnerCapabilityFilter>();

        group.MapGet("", async (
            ILocalUserProfileService profiles,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var profile = await profiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
                return Results.Json(HttpMapping.ToProfile(profile));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPatch("", async (
            PatchUserProfileRequest? body,
            ILocalUserProfileService profiles,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (body?.Values is null || body.Values.Count == 0)
                {
                    throw AgentCoreErrors.Validation("values are required.");
                }

                var profile = await profiles.UpdateLocalProfileAsync(
                        body.ExpectedRevision,
                        body.Values,
                        UserProfileValueSource.UserSet,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(HttpMapping.ToProfile(profile));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });
    }
}
