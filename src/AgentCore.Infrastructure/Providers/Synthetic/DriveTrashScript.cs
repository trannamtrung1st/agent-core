using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
namespace AgentCore.Infrastructure.Providers.Synthetic;

// Offline acceptance only: the same approval, credential admission and durable execution path as normal tools.
internal static class DriveTrashScript
{
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var last = request.Messages.ToList().FindLastIndex(m => m.Role == ModelRole.User);
        if (last < 0) return null;
        var marker = request.Messages[last].Text.Trim();
        if (marker is not ("synthetic-drive-trash: single" or "synthetic-drive-trash: batch")) return null;
        var results = request.Messages.Skip(last + 1).Where(m => m.Role == ModelRole.Tool).ToArray();
        if (results.Any(m => { using var doc = JsonDocument.Parse(m.Text); return doc.RootElement.TryGetProperty("error", out _); }))
            return Done("Drive fixture stopped after rejection or a tool failure. No additional mutation or success is claimed.");
        if (results.Length == 0) return Call(request, ToolCatalog.CapabilitiesLoad, new { query = "drive.accounts", limit = 1 });
        if (results.Length == 1) return Call(request, "drive.accounts", new { });
        using var accounts = JsonDocument.Parse(results[1].Text);
        var ready = accounts.RootElement.GetProperty("accounts").EnumerateArray().Where(a => a.GetProperty("available").GetBoolean() && a.GetProperty("synthetic").GetBoolean()).ToArray();
        if (ready.Length != 1) return Done("Choose exactly one connected bound Synthetic Drive account. This fixture never mutates a real account.");
        var account = ready[0].GetProperty("accountRef").GetString(); var epoch = ready[0].GetProperty("authEpoch").GetInt64();
        if (results.Length == 2) return Call(request, ToolCatalog.CapabilitiesLoad, new { query = "drive.list", limit = 1 });
        if (results.Length == 3) return Call(request, "drive.list", new { accountRef = account, query = "AgentCoreUat", limit = 20 });
        using var listing = JsonDocument.Parse(results[3].Text);
        var files = listing.RootElement.GetProperty("page").GetProperty("items").EnumerateArray().Where(f => !f.GetProperty("folder").GetBoolean() && !f.GetProperty("trashed").GetBoolean()).Take(marker.EndsWith("batch", StringComparison.Ordinal) ? 7 : 1).ToArray();
        if (files.Length != (marker.EndsWith("batch", StringComparison.Ordinal) ? 7 : 1)) return Done("Create the disposable Synthetic AgentCoreUat fixtures first. No mutation was requested.");
        var tool = marker.EndsWith("batch", StringComparison.Ordinal) ? "drive.trash_batch" : "drive.trash";
        if (results.Length == 4) return Call(request, ToolCatalog.CapabilitiesLoad, new { query = tool, limit = 1 });
        if (results.Length == 5) return tool == "drive.trash_batch"
            ? Call(request, tool, new { accountRef = account, authEpoch = epoch, files = files.Select(f => new { fileId = f.GetProperty("id").GetString(), expectedVersion = f.GetProperty("version").GetString() }).ToArray() })
            : Call(request, tool, new { accountRef = account, authEpoch = epoch, fileId = files[0].GetProperty("id").GetString(), expectedVersion = files[0].GetProperty("version").GetString() });
        using var outcome = JsonDocument.Parse(results[5].Text);
        if (tool == "drive.trash_batch")
        {
            var result = outcome.RootElement.GetProperty("result");
            return Done($"Synthetic app-authorized batch: requested {result.GetProperty("requestedCount")}; trashed {result.GetProperty("trashedCount")}; failed {result.GetProperty("failedCount")}; uncertain {result.GetProperty("uncertainCount")}; not attempted {result.GetProperty("notAttemptedCount")}. Only the exact approved files were eligible. Inspect uncertain or remaining files and obtain fresh approval; no blind retry or rollback.");
        }
        var receipt = outcome.RootElement.GetProperty("receipt");
        return Done($"Synthetic app-authorized file outcome: {receipt.GetProperty("status").GetString()}. Google Drive Trash is recoverable; no permanent deletion.");
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(ModelRequest r, string tool, object args) => r.Tools?.Any(t => t.Name == tool) == true
        ? [new ModelToolCallEvent(new("drive-trash-proof-" + r.Messages.Count(m => m.Role == ModelRole.Tool) + "-" + tool, tool, JsonSerializer.Serialize(args))), new ModelCompleted(ModelStopReason.ToolCalls)]
        : Done("Drive fixture requires explicitly authorized " + tool + ". No mutation is claimed.");
    private static IReadOnlyList<ModelGenerationEvent> Done(string text) => [new ModelTextDelta(text), new ModelCompleted(ModelStopReason.Completed)];
}
