using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;

namespace AgentCore.Api;

internal static class RuntimeStateEndpoints
{
    internal static void Map(RouteGroupBuilder admin)
    {
        admin.MapGet("/agent-instances/{instanceId:guid}/state", async (Guid instanceId, RuntimeStateService service, CancellationToken ct) =>
        {
            try
            {
                var state = await service.ObserveAsync(instanceId, ct);
                return Results.Ok(new RuntimeStateResponse(state.AgentInstanceId, state.ObservedAtUtc, state.FreshThroughUtc,
                    state.Availability, state.Sessions is { } s ? new(s.Open, s.Paused, s.Terminal, s.LastActivityAtUtc) : null,
                    state.HostedSessions is { } h ? new(h.Runtimes, h.Attached) : null,
                    state.Runs is { } r ? new(r.Queued, r.Executing, r.WaitingApproval, r.WaitingSignal, r.Retrying, r.Terminal) : null,
                    state.Resources.Select(r => new ResourceStateResponse(r.Kind, r.Availability, r.Sharing, r.ObservedAtUtc,
                        r.LiveContexts, r.OpenPages, r.ActiveGenerations, r.ActiveCommands, r.CleanupPending, r.Code)).ToArray(),
                    state.Issues.Select(i => new RuntimeStateIssueResponse(i.Kind, i.Code)).ToArray()));
            }
            catch (AgentCoreException e) { return Results.Problem(statusCode: e.StatusCode, title: e.Code, detail: e.Message); }
        });
    }
}
