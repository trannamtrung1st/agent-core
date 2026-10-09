using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Execution;

// Only Core identities, registered capability names and receipt codes enter this trusted block.
// Page content and model-supplied arguments never become execution facts.
internal static class RunExecutionFacts
{
    internal static string Current(Guid? runId, IReadOnlyList<string> loaded, IEnumerable<ModelMessage> receipts) =>
        Describe("current", runId, "running", null, loaded, receipts);

    internal static string Previous(AgentRun run, string? interruption)
    {
        AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var receipts);
        return Describe("previous", run.AgentRunId, run.Status.ToString(), interruption, run.LoadedCapabilityIds, receipts ?? []);
    }

    private static string Describe(string scope, Guid? runId, string status, string? interruption,
        IReadOnlyList<string> loaded, IEnumerable<ModelMessage> receipts)
    {
        var outcomes = new Dictionary<(string Tool, string Outcome), int>();
        foreach (var receipt in receipts.Where(r => r.Role == ModelRole.Tool && r.Name is not null))
        {
            if (!ToolRegistry.TryGet(receipt.Name!, out _)) continue;
            try
            {
                using var json = JsonDocument.Parse(receipt.Text);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var outcome = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    ? error.GetString() switch
                    {
                        "invalid" or "invalid_reference" or "ValidationError" => "invalid_arguments",
                        "invalid_tool_strategy_blocked" => "invalid_strategy_blocked",
                        "forbidden" or "target_denied" or "user_intervention_required" => "policy_blocked",
                        "not_found" or "ambiguous_target" => "target_unresolved",
                        _ => "failed"
                    }
                    : root.TryGetProperty("status", out var success) && success.ValueKind == JsonValueKind.String && success.GetString() == "ok" ? "succeeded" : null;
                if (outcome is not null) outcomes[(receipt.Name!, outcome)] = outcomes.GetValueOrDefault((receipt.Name!, outcome)) + 1;
            }
            catch (JsonException) { }
        }
        return "Trusted Core execution facts (" + scope + " Run; do not attribute previous facts to the current Run): "
            + JsonSerializer.Serialize(new
            {
                scope, runId, status,
                interruption = interruption is "userSteer" or "userCancel" or "disconnect" ? interruption : null,
                loadedCapabilities = loaded.Where(c => ToolRegistry.TryGet(c, out _)).Distinct().Order(StringComparer.Ordinal).Take(64),
                toolOutcomes = outcomes.OrderBy(o => o.Key.Tool, StringComparer.Ordinal).ThenBy(o => o.Key.Outcome, StringComparer.Ordinal).Take(64)
                    .Select(o => new { tool = o.Key.Tool, outcome = o.Key.Outcome, count = o.Value }),
                interpretation = "Successful type/fill_form receipts establish field actions. Successful fill_credential establishes a protected field fill. Successful click establishes a click, not submission acceptance or authentication. These receipts do not establish sign-in. Confirm authenticated state with subsequent observations before reporting sign-in. A capability loaded in the previous Run is not loaded in the current Run."
            });
    }
}
