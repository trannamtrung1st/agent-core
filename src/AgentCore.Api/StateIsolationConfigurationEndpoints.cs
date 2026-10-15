using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Api;

internal static class StateIsolationConfigurationEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record PresetRequest(string Preset);
    private sealed record ApplyRequest(IsolationOwnerRevisions Expected, IsolationDraft Draft, string Fingerprint);
    internal static void Map(RouteGroupBuilder admin)
    {
        var group = admin.MapGroup("/agent-instances/{instanceId:guid}/isolation");
        group.MapGet("", (Guid instanceId, StateIsolationConfigurationService service, CancellationToken ct) => Respond(() => service.ReadAsync(instanceId, ct)));
        group.MapPost("/preset", (Guid instanceId, JsonElement body, StateIsolationConfigurationService service, CancellationToken ct) => Respond(() =>
            service.PresetAsync(instanceId, Read<PresetRequest>(body).Preset, ct)));
        group.MapPost("/preview", (Guid instanceId, JsonElement body, StateIsolationConfigurationService service, CancellationToken ct) => Respond(() =>
            service.PreviewAsync(instanceId, Read<IsolationDraft>(body), ct)));
        group.MapPost("/apply", (Guid instanceId, JsonElement body, StateIsolationConfigurationService service, CancellationToken ct) => Respond(() =>
        {
            var input = Read<ApplyRequest>(body);
            return service.ApplyAsync(instanceId, input.Expected, input.Draft, input.Fingerprint, ct);
        }));
    }
    private static T Read<T>(JsonElement body) => body.Deserialize<T>(Json) ?? throw new JsonException();
    private static async Task<IResult> Respond<T>(Func<ValueTask<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (AgentCoreException error) { return Results.Problem(statusCode: error.StatusCode, title: error.Code, detail: error.Message); }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException or InvalidOperationException or KeyNotFoundException)
        { return Results.Problem(statusCode: 400, title: "Validation", detail: "State & Isolation request does not match its required shape."); }
    }
}
