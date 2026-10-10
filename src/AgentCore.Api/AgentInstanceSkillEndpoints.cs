using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Admin;
using AgentCore.Contracts.Http;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
namespace AgentCore.Api;

internal static class AgentInstanceSkillEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public static void Map(RouteGroupBuilder admin)
    {
        var g = admin.MapGroup("/agent-instances/{instanceId:guid}/skills");
        g.MapGet("", (Guid instanceId, AgentInstanceSkillService s, CancellationToken ct) => Respond(async () =>
            (await s.ListAsync(instanceId, ct: ct)).Select(v => Wire(v with { Procedure = "" })).ToArray()));
        g.MapGet("/{key}", (Guid instanceId, string key, AgentInstanceSkillService s, CancellationToken ct) => Respond(() => Read(s.InspectAsync(instanceId, key, ct: ct))));
        g.MapPost("", (Guid instanceId, JsonElement body, AgentInstanceSkillService s, CancellationToken ct) => Respond(() => Write(s.WriteAsync(instanceId, "create", input: InstanceSkillTools.ReadInput(body), ct: ct))));
        g.MapPatch("/{key}", (Guid instanceId, string key, JsonElement body, AgentInstanceSkillService s, CancellationToken ct) => Respond(() => Write(s.WriteAsync(instanceId, "update", key, Revision(body), InstanceSkillTools.ReadInput(body), ct: ct))));
        g.MapPut("/{key}/enabled", (Guid instanceId, string key, JsonElement body, AgentInstanceSkillService s, CancellationToken ct) => Respond(() => Write(s.WriteAsync(instanceId, "set_enabled", key, Revision(body), enabled: body.GetProperty("enabled").GetBoolean(), ct: ct))));
        g.MapDelete("/{key}/enabled-override", (Guid instanceId, string key, long expectedRevision, AgentInstanceSkillService s, CancellationToken ct) => Respond(() => Write(s.WriteAsync(instanceId, "reset_enabled", key, expectedRevision, ct: ct))));
        g.MapDelete("/{key}", (Guid instanceId, string key, long expectedRevision, AgentInstanceSkillService s, CancellationToken ct) => Respond(() => Write(s.WriteAsync(instanceId, "delete", key, expectedRevision, ct: ct))));
        g.MapPost("/{key}/customize", (Guid instanceId, string key, JsonElement body, AgentInstanceSkillService s, CancellationToken ct) => Respond(() => Customize(s.CustomizeAsync(instanceId, key, Revision(body), ct: ct))));
    }
    private static AgentInstanceSkillResponse Wire(InstanceSkillView v) => new(v.Key, v.Origin.ToString(), v.Name, v.Description,
        v.Procedure, v.Projection.ToString(), v.Enabled, v.RequiredCapabilities, v.Revision, v.DefinitionVersion,
        v.SourceDefinitionId, v.SourceDefinitionVersion, v.SourceDefinitionSkillId, v.MissingCapabilities,
        v.CreatedBy?.ToString(), v.CreatedAt, v.UpdatedAt, v.EnabledOverride);
    private static async ValueTask<AgentInstanceSkillResponse> Read(ValueTask<InstanceSkillView> action) => Wire(await action);
    private static async ValueTask<AgentInstanceSkillResponse> Write(ValueTask<InstanceSkillView> action) => Wire(await action);
    private static async ValueTask<AgentInstanceSkillCustomizeResponse> Customize(ValueTask<InstanceSkillCustomization> action)
    {
        var result = await action;
        return new(Wire(result.InstanceSkill), new(result.DefinitionSkill.Key, result.DefinitionSkill.Enabled, result.DefinitionSkill.Revision));
    }
    private static long Revision(JsonElement body) => body.GetProperty("expectedRevision").GetInt64();
    private static async Task<IResult> Respond<T>(Func<ValueTask<T>> action)
    {
        try { return Results.Json(await action(), Json); }
        catch (AgentCoreException e) { return Results.Problem(statusCode: e.StatusCode, title: e.Code, detail: e.Message); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return Results.Problem(statusCode: 400, title: "Validation", detail: "Skill arguments do not match the required shape."); }
    }
}
