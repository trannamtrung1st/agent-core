using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Sessions;

namespace AgentCore.Api;

internal static class AgentInstanceSettingsEndpoints
{
    public static void Map(RouteGroupBuilder admin)
    {
        var group = admin.MapGroup("/agent-instances/{instanceId:guid}/settings");
        group.MapGet("", (Guid instanceId, AgentInstanceSettingsService service, CancellationToken ct) => Respond(() => service.ReadAllAsync(instanceId, ct)));
        group.MapGet("/{section}", (Guid instanceId, string section, AgentInstanceSettingsService service, CancellationToken ct) =>
            Respond(() => service.ReadAsync(instanceId, section, ct)));
        group.MapPatch("/{section}", (Guid instanceId, string section, JsonElement body, AgentInstanceSettingsService service, CancellationToken ct) => Respond(() =>
        {
            if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Any(p => p.Name is not ("expectedInstanceRevision" or "set" or "clear")))
                throw AgentCoreErrors.Validation("Settings request must contain expectedInstanceRevision, set and clear only.");
            var set = body.TryGetProperty("set", out var fields) ? fields.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal) : [];
            var clear = body.TryGetProperty("clear", out var clears) ? clears.EnumerateArray().Select(p => p.GetString() ?? throw new JsonException()).ToArray() : [];
            return service.PatchAsync(instanceId, section, body.GetProperty("expectedInstanceRevision").GetInt64(), set, clear, ct);
        }));
    }
    private static async Task<IResult> Respond<T>(Func<ValueTask<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (AgentCoreException e) { return Results.Problem(statusCode: e.StatusCode, title: e.Code, detail: e.Message); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { return Results.Problem(statusCode: 400, title: "Validation", detail: "Settings request does not match the required shape."); }
    }
}
