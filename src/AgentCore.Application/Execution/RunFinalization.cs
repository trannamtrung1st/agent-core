using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Domain.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Execution;

internal static class RunFinalization
{
    internal const string BrowserLifecycleInstruction = "Only observed dialog_pending justifies native dialog recovery; target_missing does not. Native dialog_pending is a blocked decision, not browser unavailability. Inspect with authorized browser.dialog, resolve only when justified by the requested action, then observe the application before proceeding. Do not repeat the triggering click. A closed browser profile can retain authentication: sign-out requires independent application evidence. Logout and closure are independent outcomes. Attempt an independently requested permitted browser close even when logout fails, without asking permission again; honor closure conditional on verified logout, preservation instructions and cancellation/deadlines. Never close without user intent. Report both outcomes and cleanup failures or uncertainty truthfully.";
    internal const string CleanupInstruction = "The remaining browser work budget is reserved for requested cleanup and verification. Stop starting new substantive work. Resolve only dialogs justified by the action the user requested; never automatically accept an unknown confirmation. Use authorized visual recovery when semantic targeting is inadequate and capture capacity remains. Verify sign-out with an application observation independently of browser closure. Failed logout must not suppress an independently requested permitted close unless closure was conditional on verified logout or the user requested preservation. Close only if requested, then report logout and closure separately in a truthful final or partial result before the deadline.";
    internal static bool CleanupTool(string name) => name is ToolCatalog.WorkComplete or ToolCatalog.BrowserFind or ToolCatalog.BrowserSnapshot or ToolCatalog.BrowserDialog or ToolCatalog.BrowserClick or ToolCatalog.BrowserClose or ToolCatalog.BrowserTabs
        or ToolCatalog.BrowserHover or "browser.scroll" or ToolCatalog.BrowserWait
        or "browser.verify" or ToolCatalog.BrowserPressKey or ToolCatalog.BrowserScreenshot or ToolCatalog.BrowserVisionMouse or ToolCatalog.BrowserConfiguration;
    internal static bool InCleanup(AgentRun run)
    {
        var state=AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint);
        return state.Phase==RunPhase.Cleanup || state.CleanupStartedAtMessage is not null;
    }
    internal static bool CleanupBrowser(string name,string arguments,bool downloads=false)
    {
        if(downloads || !CleanupTool(name) || !ToolCatalog.IsBrowserTool(name))return false;
        if(name!=ToolCatalog.BrowserTabs)return true;
        try { using var json=JsonDocument.Parse(arguments);return !json.RootElement.TryGetProperty("operation",out var operation) || operation.GetString()!="new"; }
        catch(Exception e) when(e is JsonException or InvalidOperationException) { return false; }
    }
    internal static bool CleanupCall(ModelToolCall call)
    {
        if(call.Name!=ToolCatalog.RequestAuthorization)return CleanupTool(call.Name) && (call.Name!=ToolCatalog.BrowserTabs || CleanupBrowser(call.Name,call.ArgumentsJson));
        try { return TenantApprovalToolContract.Parse(call).Browser is { } browser && CleanupBrowser(browser.Tool,browser.ArgumentsJson,browser.DownloadArtifacts); }
        catch(Exception e) when(e is JsonException or ArgumentException or InvalidOperationException) { return false; }
    }
    internal static IReadOnlyList<ModelToolDefinition> CleanupTools(IEnumerable<ModelToolDefinition> offered)
    {
        var result=new List<ModelToolDefinition>();
        foreach(var tool in offered)
        {
            if(CleanupTool(tool.Name)) { result.Add(tool);continue; }
            if(tool.Name!=ToolCatalog.RequestAuthorization)continue;
            var root=JsonNode.Parse(tool.ParametersJson);var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var branch in root?["oneOf"]?.AsArray() ?? [])
                if(branch?["properties"]?["operation"]?["enum"]?.AsArray().Any(v=>v?.GetValue<string>()=="run_browser_command")==true)
                    foreach(var command in branch["properties"]!["command"]!["oneOf"]!.AsArray())
                        if(command?["properties"]?["tool"]?["enum"]?[0]?.GetValue<string>() is { } name && CleanupTool(name))names.Add(name);
            if(names.Count==0)continue;
            var schema=TenantBrowserAuthorizationContract.Schema(names.Order(StringComparer.Ordinal).ToArray());
            foreach(var command in schema["properties"]!["command"]!["oneOf"]!.AsArray())
            {
                command!["properties"]!["downloadArtifacts"]=new JsonObject { ["type"]="boolean",["const"]=false };
                if(command["properties"]!["tool"]!["enum"]![0]!.GetValue<string>()==ToolCatalog.BrowserTabs && command["properties"]!["arguments"]!["properties"]?["operation"]?["enum"] is JsonArray operations)
                    for(var index=operations.Count-1;index>=0;index--)if(operations[index]!.GetValue<string>()=="new")operations.RemoveAt(index);
            }
            result.Add(tool with { Description="Request current exact human authorization for one offered Browser cleanup or verification command. Preserve its exact arguments. No new substantive work, tab creation, download publication or other operation is authorized. Approval is separate from execution; report observed cleanup and uncertainty truthfully.",
                ParametersJson=new JsonObject { ["type"]="object",["oneOf"]=new JsonArray(schema) }.ToJsonString() });
        }
        return result;
    }
    internal const string SandboxRecoveryInstruction = "Sandbox recovery is required. Durable evidence is in the tool receipt. Explain its observed lifecycle, possible complete or partial writes already visible in canonical /home and /working, and safest owner action. Files do not synchronize or commit after execution; unconfirmed termination may allow further writes. Do not repeat commands or switch profiles. New profile and execution evidence overrides historical Memory or Experience about installed SDKs/packages. Honor instructions not to run again. Unknown outcomes remain uncertain.";
    internal const string BackgroundInstruction = "The work phase has ended. Call work.complete with a truthful summary of 1 to 2000 characters using verified results from existing durable tool receipts. Preserve important findings and uncertainty within that limit. A validation error permits at most one corrected completion within the remaining budget; follow its field-specific feedback. No substantive tools or new external effects are permitted. Do not invent research, artifacts or successful effects. Use NeedsAttention for incomplete or uncertain work; NoAction is only for a verified quiet outcome. Ordinary prose does not complete a background Run.";
    internal static ProviderFailure MissingCompletion() => new(ProviderErrorCode.InvalidResponse,
        "Background work requires an explicit completion outcome.", FailureReason: ProviderFailureReason.BackgroundCompletionRequired,
        ResponseChannel: ProviderResponseChannel.BackgroundCompletion);
    internal static ProviderFailure InvalidCompletion() => new(ProviderErrorCode.InvalidResponse,
        "Background work returned an invalid completion outcome.", FailureReason: ProviderFailureReason.BackgroundCompletionInvalid,
        ResponseChannel: ProviderResponseChannel.BackgroundCompletion);
    internal const string Instruction = "The work phase has ended. Generate a final reply only from existing durable tool receipts. No tools or new external effects are permitted. Distinguish recorded actions from unverified task outcomes. A browser closure does not establish sign-out. Report incomplete or uncertain work accurately.";
    internal static DateTimeOffset? Deadline(DateTimeOffset now, DateTimeOffset? overallDeadline, TimeSpan reserve)
    {
        if (reserve == TimeSpan.Zero) return overallDeadline;
        var reservedDeadline = now + reserve;
        return overallDeadline is null || reservedDeadline < overallDeadline ? reservedDeadline : overallDeadline;
    }
    internal static bool IsReason(string? reason) => reason is "protocolRepair" or "completionRequired" or "runDeadline" or "providerTimeout" or "providerCancelled" or "browserBlocked" or "stepLimit" or "checkpointCapacity" or "outputLimit" or "cleanupBlocked" or "sandboxRecovery";
    internal static string? ObservedRecovery(IEnumerable<ModelMessage> messages) => messages.Any(m => m.Role == ModelRole.Tool && m.Name?.StartsWith("sandbox.", StringComparison.Ordinal) == true && IsSandboxRecovery(m.Text)) ? "sandboxRecovery" : null;
    private static bool IsSandboxRecovery(string text)
    {
        try { using var doc=JsonDocument.Parse(text); return doc.RootElement.TryGetProperty("error",out var error) && error.ValueKind==JsonValueKind.String && error.GetString()=="recovery_required"; }
        catch (JsonException) { return false; }
    }
    internal static bool HasEvidence(IEnumerable<ModelMessage> messages) => messages.Any(m =>
    {
        if (m.Role != ModelRole.Tool) return false;
        try
        {
            using var doc = JsonDocument.Parse(m.Text);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && (!root.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null)
                && (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() is "ok" or "closed" or "already_closed"
                    || root.TryGetProperty("outcome", out var outcome) && outcome.ValueKind == JsonValueKind.String && outcome.GetString() == "sent");
        }
        catch (JsonException) { return false; }
    });
    internal static string? RecoveryReason(ProviderFailure failure) => failure.Code switch
    {
        ProviderErrorCode.Timeout => "providerTimeout",
        _ => null
    };
    internal static ProviderFailure DeadlineFailure() => new(ProviderErrorCode.Timeout, "Run deadline reached.", FailureReason: "runDeadline");
}
