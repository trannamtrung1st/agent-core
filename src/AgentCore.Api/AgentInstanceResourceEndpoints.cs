using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Api;

internal static class AgentInstanceResourceEndpoints
{
    public static void Map(RouteGroupBuilder admin)
    {
        var g = admin.MapGroup("/agent-instances/{instanceId:guid}/resources");
        g.MapGet("", (Guid instanceId, AgentInstanceResourceService s, CancellationToken ct) => Respond(() => s.ListAsync(instanceId, ct)));
        g.MapGet("/{key}", (Guid instanceId, string key, AgentInstanceResourceService s, CancellationToken ct) => Respond(async () =>
            (await s.ListAsync(instanceId, ct)).Resources.SingleOrDefault(r => r.Key == key) ?? throw AgentCoreErrors.NotFound("Resource was not found.")));
        g.MapGet("/{key}/content", async (Guid instanceId, string key, AgentInstanceResourceService s, CancellationToken ct) =>
        {
            try {
                var result = await s.ReadContentAsync(instanceId, key, ct);
                return Results.File(result.Bytes, result.Resource.MediaType, Path.GetFileName(result.Resource.LogicalPath));
            }
            catch (AgentCoreException e) { return Results.Problem(statusCode: e.StatusCode, title: e.Code, detail: e.Message); }
        });
        g.MapPost("", (Guid instanceId, HttpRequest request, AgentInstanceResourceService s, CancellationToken ct) => Upload(instanceId, null, request, s, ct));
        g.MapPost("/{key}/copy", (Guid instanceId, string key, ResourceCopyRequest body, AgentInstanceResourceService s, CancellationToken ct) =>
            Respond(() => s.CopyAsync(instanceId, key, body.ExpectedInstanceRevision, body.LogicalPath, ct)));
        g.MapPatch("/{key}", (Guid instanceId, string key, HttpRequest request, AgentInstanceResourceService s, CancellationToken ct) => Upload(instanceId, key, request, s, ct));
        g.MapPut("/{key}/enabled", (Guid instanceId, string key, JsonElement body, AgentInstanceResourceService s, CancellationToken ct) => Respond(() =>
            s.SetEnabledAsync(instanceId, key, body.GetProperty("expectedInstanceRevision").GetInt64(), body.GetProperty("expectedRevision").GetInt64(), body.GetProperty("enabled").GetBoolean(), ct: ct)));
        g.MapDelete("/{key}/enabled-override", (Guid instanceId, string key, long expectedInstanceRevision, long expectedRevision, AgentInstanceResourceService s, CancellationToken ct) => Respond(() =>
            s.SetEnabledAsync(instanceId, key, expectedInstanceRevision, expectedRevision, null, ct: ct)));
        g.MapDelete("/{key}", (Guid instanceId, string key, long expectedInstanceRevision, long expectedRevision, AgentInstanceResourceService s, CancellationToken ct) => Respond(() =>
            s.DeleteAsync(instanceId, key, expectedInstanceRevision, expectedRevision, ct: ct)));
    }
    private sealed record ResourceCopyRequest(long ExpectedInstanceRevision, string LogicalPath);
    private static Task<IResult> Upload(Guid id, string? key, HttpRequest request, AgentInstanceResourceService service, CancellationToken ct) => Respond(async () =>
    {
        if (!request.HasFormContentType) throw AgentCoreErrors.Validation("Use a multipart form with one file.");
        var form = await request.ReadFormAsync(ct);
        if (form.Files.Count != 1) throw AgentCoreErrors.Validation("Exactly one resource file is required.");
        var file = form.Files[0];
        DefinitionResourcePolicies.ValidateFileName(file.FileName);
        DefinitionResourcePolicies.ValidateContentSize(file.Length);
        await using var input = file.OpenReadStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[65536]; int count;
        while ((count = await input.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > AgentResourceLimits.MaxItemBytes) throw AgentCoreErrors.Validation("Resource exceeds the per-item size limit.");
            buffer.Write(chunk, 0, count);
        }
        var parsed = key is null ? ((string Origin, Guid Id)?)null : AgentInstanceResourceService.ParseKey(key);
        if (parsed is { Origin: "definition" }) throw AgentCoreErrors.Validation("Inherited resource content is read-only.");
        if (!Enum.TryParse<AgentDefinitionResourceKind>(form["kind"], out var kind) || !Enum.IsDefined(kind)) throw AgentCoreErrors.Validation("Resource kind is invalid.");
        return await service.UpsertAsync(id, long.Parse(form["expectedInstanceRevision"]!), parsed?.Id,
            key is null ? null : long.Parse(form["expectedRevision"]!), form["logicalPath"]!, kind,
            file.ContentType, buffer.ToArray(), !bool.TryParse(form["enabled"], out var enabled) || enabled, ct: ct);
    });
    private static async Task<IResult> Respond<T>(Func<ValueTask<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (AgentCoreException e) { return Results.Problem(statusCode: e.StatusCode, title: e.Code, detail: e.Message); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or InvalidDataException)
        { return Results.Problem(statusCode: 400, title: "Validation", detail: "Resource request does not match the required shape."); }
    }
}
