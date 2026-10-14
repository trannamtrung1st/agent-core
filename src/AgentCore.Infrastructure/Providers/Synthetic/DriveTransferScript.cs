using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
namespace AgentCore.Infrastructure.Providers.Synthetic;

// Compose survival fixture uses normal admitted conversational tools, never a source-specific mutating HTTP route.
internal static class DriveTransferScript
{
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var last = request.Messages.ToList().FindLastIndex(m => m.Role == ModelRole.User);
        if (last < 0 || request.Messages[last].Text.Trim() != "synthetic-drive: compose-upload") return null;
        var results = request.Messages.Skip(last + 1).Where(m => m.Role == ModelRole.Tool).ToArray();
        if (results.Any(m => { using var doc = JsonDocument.Parse(m.Text); return doc.RootElement.TryGetProperty("error", out _); })) return Done("Compose Drive fixture failed; no success is claimed.");
        if (results.Length == 0) return Call(request, ToolCatalog.CapabilitiesLoad, new { query = "workspace.read", limit = 1 });
        if (results.Length == 1) return Call(request, ToolCatalog.WorkspaceRead, new { path = "/home/binary.dat" });
        if (results.Length == 2) return Call(request, ToolCatalog.CapabilitiesLoad, new { query = "drive.accounts", limit = 1 });
        if (results.Length == 3) return Call(request, "drive.accounts", new { });
        using var accounts = JsonDocument.Parse(results[3].Text);
        var ready = accounts.RootElement.GetProperty("accounts").EnumerateArray().Where(a => a.GetProperty("available").GetBoolean() && a.GetProperty("synthetic").GetBoolean()).ToArray();
        if (ready.Length != 1) return Done("Compose Drive fixture requires one explicitly bound Synthetic account.");
        if (results.Length == 4) return Call(request, ToolCatalog.CapabilitiesLoad, new { query = "drive.upload", limit = 1 });
        if (results.Length == 5)
        {
            using var source = JsonDocument.Parse(results[1].Text);
            return Call(request, "drive.upload", new { accountRef = ready[0].GetProperty("accountRef").GetString(), authEpoch = ready[0].GetProperty("authEpoch").GetInt64(),
                path = "/home/binary.dat", expectedSha256 = source.RootElement.GetProperty("homeItem").GetProperty("Sha256Hex").GetString(), name = "compose-binary.dat" });
        }
        return Done(results[5].Text);
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(ModelRequest request, string tool, object args) => request.Tools?.Any(t => t.Name == tool) == true
        ? [new ModelToolCallEvent(new("drive-transfer-proof-" + request.Messages.Count(m => m.Role == ModelRole.Tool) + "-" + tool, tool, JsonSerializer.Serialize(args))), new ModelCompleted(ModelStopReason.ToolCalls)]
        : Done("Compose Drive fixture requires explicitly authorized " + tool + ".");
    private static IReadOnlyList<ModelGenerationEvent> Done(string text) => [new ModelTextDelta(text), new ModelCompleted(ModelStopReason.Completed)];
}
