using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    private sealed record ConfigurationRemedy(string Option, string Operation, string Section,
        IReadOnlyDictionary<string, JsonElement> Set, IReadOnlyList<string> Clear, string Target, string Goal,
        long ExpectedInstanceRevision);

    private static string? DiagnosisTarget(JsonElement args)
    {
        if (args.TryGetProperty("target", out var target)) return target.GetString();
        if (!args.TryGetProperty("goal", out var goal)) return null;
        var text = goal.GetString()!.ToLowerInvariant();
        return text.Contains("email") ? ToolCatalog.EmailSend : text.Contains("sign in") ? ToolCatalog.BrowserFillCredential
            : text.Contains("browser") || text.Contains("look up") ? "browser.navigate"
            : text.Contains("brief") || text.Contains("concise") ? "settings.patch"
            : text.Contains("reminder") || text.Contains("automation") ? ToolCatalog.AutomationCreate : null;
    }

    private static string DiagnosisOption(Guid id, ManagementPolicyView policy, string target, string goal,
        string section, IReadOnlyDictionary<string, JsonElement> set) => Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, policy.InstanceRevision, policy.PolicyRevision,
                target, goal, section, set = set.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value), clear = Array.Empty<string>() }))));

    private async ValueTask<ConfigurationRemedy?> DiagnoseRemedyAsync(Guid id, ManagementPolicyView policy,
        AgentDefinition admitted, JsonElement args, CancellationToken ct)
    {
        var target = DiagnosisTarget(args);
        if (target is null || !policy.Effective.Allows(HarnessManagementScope.Settings)
            || admitted.SelfManagement?.Allows(HarnessManagementScope.Settings) == false) return null;
        var goal = args.TryGetProperty("goal", out var g) ? g.GetString()! : target;
        string section;
        Dictionary<string, JsonElement> set;
        if (target == "settings.patch" && (goal.Contains("brief", StringComparison.OrdinalIgnoreCase) || goal.Contains("concise", StringComparison.OrdinalIgnoreCase)))
        {
            section = "conversationPolicy";
            set = new() { ["responseLength"] = JsonSerializer.SerializeToElement("concise") };
        }
        else
        {
            var baseline = _agentDefinitions is null ? admitted : await _agentDefinitions.GetAsync(admitted.Id, admitted.Version, ct) ?? admitted;
            if (!ToolRegistry.TryGet(target, out var descriptor) || !descriptor.DefinitionAuthorizable
                || !RolePermissions.AllowsTool(baseline, target) || RolePermissions.AllowsTool(admitted, target)
                || !_configurationGate.IsConfigured(target)) return null;
            section = "capabilities";
            var state = await instanceSettings!.ReadAsync(id, section, ct);
            var selected = JsonSerializer.SerializeToElement(state.Effective["selected"]).EnumerateArray().Select(v => v.GetString()!)
                .Append(target).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            set = new() { ["selected"] = JsonSerializer.SerializeToElement(selected) };
        }
        await instanceSettings!.ValidatePatchAsync(id, section, policy.InstanceRevision, set, [], ct);
        return new(DiagnosisOption(id, policy, target, goal, section, set), "settings.patch", section, set, [], target, goal, policy.InstanceRevision);
    }

    private async ValueTask ValidateTargetedRemedyAsync(Guid id, ManagementPolicyView policy,
        AgentDefinition admitted, JsonElement args, CancellationToken ct)
    {
        var offered = await DiagnoseRemedyAsync(id, policy, admitted, args, ct);
        var set = args.GetProperty("set").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        if (offered is null || args.GetProperty("option").GetString() != offered.Option
            || args.GetProperty("section").GetString() != offered.Section || args.GetProperty("clear").GetArrayLength() != 0
            || set.Count != offered.Set.Count || offered.Set.Any(p => !set.TryGetValue(p.Key, out var value) || !JsonElement.DeepEquals(value, p.Value)))
            throw AgentCoreErrors.Conflict("The diagnosed exact change is unknown or stale. Inspect the current blocker again.");
    }
}
