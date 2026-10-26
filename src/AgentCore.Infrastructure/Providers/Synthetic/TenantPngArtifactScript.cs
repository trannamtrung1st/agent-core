using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

// Opt-in deterministic fixture. All commands/publications use the canonical tenant approval owner.
internal static class TenantPngArtifactScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var publishExisting = SyntheticTaskContext.Text(request).StartsWith("synthetic-png-publish-existing", StringComparison.Ordinal);
        var transcript = request.Interaction ?? request.Messages;
        var results = transcript.Where(m => m.Role == ModelRole.Tool && m.EvidenceKind != EvidenceMessageKind.Historical
            && m.ToolCallId?.StartsWith("png-proof-", StringComparison.Ordinal) == true).ToArray();
        // Approval continuation has its own trusted task text; the current checkpoint owns the exact fixture calls.
        if (!SyntheticTaskContext.Text(request).StartsWith("synthetic-png", StringComparison.Ordinal) && results.Length == 0) return null;
        var skipsCommand = publishExisting || results.FirstOrDefault()?.ToolCallId == "png-proof-1";
        var offset = skipsCommand ? 1 : 0;
        var artifacts = new List<ModelResponseBlock>();
        for (var index = 0; index < results.Length; index++)
        {
            using var doc = JsonDocument.Parse(results[index].Text); var result = doc.RootElement;
            var completed = result.TryGetProperty("outcome", out var outcome) && outcome.GetString() == "completed";
            if (index + offset == 0)
            {
                if (!completed || !result.TryGetProperty("sandbox", out var sandbox) || sandbox.ValueKind != JsonValueKind.Object
                    || !sandbox.TryGetProperty("exitCode", out var exit) || exit.ValueKind != JsonValueKind.Number || exit.GetInt32() != 0)
                    return Finish("PNG proof stopped before validated command completion. No command was replayed.");
            }
            else
            {
                if (!completed || !result.TryGetProperty("operationId", out var id) || id.ValueKind != JsonValueKind.String)
                    return Finish("PNG proof stopped before verified publication. No publication was replayed.");
                artifacts.Add(new(ModelResponseBlockKind.ArtifactReference, ArtifactId: id.GetString()));
            }
        }
        if (results.Length + offset == 3)
            return [new ModelSemanticResponseReady(new("PNG files created in working and home, validated and published.", new(ModelSpeechMode.None, null), artifacts)), new ModelCompleted(ModelStopReason.Completed)];
        var tool = request.Tools?.SingleOrDefault(t => t.Name == ToolCatalog.RequestAuthorization);
        if (tool is null) return Finish("PNG proof requires an offered exact authorization interface.");
        object arguments;
        if (results.Length + offset == 0)
        {
            using var schema = JsonDocument.Parse(tool.ParametersJson);
            var profile = Profile(schema.RootElement);
            if (profile is null) return Finish("PNG proof requires an authorized, verified tenant Sandbox profile.");
            const string program = "import struct,zlib; from pathlib import Path; chunk=lambda t,d:struct.pack('>I',len(d))+t+d+struct.pack('>I',zlib.crc32(t+d)&0xffffffff); png=b'\\x89PNG\\r\\n\\x1a\\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',64,32,8,2,0,0,0))+chunk(b'IDAT',zlib.compress((b'\\x00'+b'\\x20\\x80\\xe0'*64)*32))+chunk(b'IEND',b''); Path('/working/plot.png').write_bytes(png); Path('/home/plot.png').write_bytes(png); print('Validated PNG 64x32')";
            arguments = new { operation = "run_sandbox_command", profileId = profile,
                command = new { argv = new[] { "python", "-I", "-c", program }, cwd = "/working", timeoutSeconds = 30, background = false } };
        }
        else arguments = new { operation = "publish_workspace_artifact", artifact = new { path = results.Length + offset == 1 ? "/working/plot.png" : "/home/plot.png",
            displayName = results.Length + offset == 1 ? "working-plot.png" : "home-plot.png", contentType = "image/png" } };
        return [new ModelToolCallEvent(new("png-proof-" + (results.Length + offset), tool.Name, JsonSerializer.Serialize(arguments))), new ModelCompleted(ModelStopReason.ToolCalls)];
    }
    private static IReadOnlyList<ModelGenerationEvent> Finish(string text) => [new ModelTextDelta(text), new ModelCompleted(ModelStopReason.Completed)];
    private static string? Profile(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("properties", out var properties) && properties.TryGetProperty("operation", out var operation)
                && operation.TryGetProperty("enum", out var operations) && operations.EnumerateArray().Any(v => v.GetString() == "run_sandbox_command")
                && properties.TryGetProperty("profileId", out var profile) && profile.TryGetProperty("enum", out var ids)) return ids[0].GetString();
            foreach (var item in node.EnumerateObject()) if (Profile(item.Value) is { } found) return found;
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) if (Profile(item) is { } found) return found;
        return null;
    }
}
