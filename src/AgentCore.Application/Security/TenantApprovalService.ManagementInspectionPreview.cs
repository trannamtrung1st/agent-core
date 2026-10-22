using System.Text.Json;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public sealed partial class TenantApprovalService
{
    private static IReadOnlyList<TenantApprovalFieldChange> ManagementInspectionChanges(JsonElement data)
    {
        var result = new List<TenantApprovalFieldChange>();
        static string Text(JsonElement value) => value.ValueKind switch {
            JsonValueKind.Null => "Inherit Agent default", JsonValueKind.String => value.GetString()!,
            JsonValueKind.True => "Yes", JsonValueKind.False => "No",
            JsonValueKind.Array => string.Join("\n", value.EnumerateArray().Select(Text)), _ => value.ToString() };
        void Add(string label, string text) => result.Add(new(label, null, string.IsNullOrEmpty(text) ? "None" : text, false));
        static string Label(string value) => System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(value, "([A-Z])([A-Z][a-z])", "$1 $2"), "([a-z0-9])([A-Z])", "$1 $2");
        void Field(string name, string label) { if (data.TryGetProperty(name, out var value)) Add(label, Text(value)); }
        Field("activeDefinitionVersion", "Active Definition version"); Field("currentRunDefinitionVersion", "Current Run Definition version");
        Field("policyRevision", "Management policy revision"); Field("mode", "Management mode"); Field("scopes", "Delegated areas");
        Field("instructions", "Agent instructions"); Field("instructionsTruncated", "Instruction excerpt");
        Field("authoringEligibleTools", "Eligible tools for future authoring"); Field("activeDefinitionAuthorizedCapabilities", "Definition capability ceiling"); Field("activeAgentSelectedCapabilities", "Current Agent capability selection");
        if (data.TryGetProperty("settings", out var settings))
            foreach (var section in settings.EnumerateArray())
                foreach (var field in section.GetProperty("effective").EnumerateObject())
                    Add("Settings · " + Label(section.GetProperty("section").GetString()!) + " · " + Label(field.Name),
                        Text(field.Value) + "\nSource: " + section.GetProperty("sources").GetProperty(field.Name).GetString());
        foreach (var group in new[] { ("skills", "Skill"), ("resources", "Resource"), ("knowledge", "Knowledge resource") })
            if (data.TryGetProperty(group.Item1, out var items))
                foreach (var item in items.EnumerateArray())
                {
                    var key = item.GetProperty("key").GetString()!;
                    var fields = group.Item1 == "skills" ? new[] { "name", "description", "origin", "projection", "enabled", "enabledChoice", "revision", "missingCapabilities" }
                        : new[] { "logicalPath", "kind", "mediaType", "byteLength", "enabled", "revision", "contentSha256" };
                    Add(group.Item2 + " · " + key, string.Join("\n", fields.Select(name => Label(name) + ": " + Text(item.GetProperty(name)))));
                }
        if (data.TryGetProperty("bindings", out var bindings))
            foreach (var item in bindings.EnumerateArray())
                Add("Connection · " + item.GetProperty("reference").GetString(), item.GetProperty("displayName").GetString() + "\nKind: " + item.GetProperty("kind").GetString()
                    + "\nPurpose: " + item.GetProperty("purpose").GetString() + "\nRevision: " + item.GetProperty("revision"));
        if (data.TryGetProperty("skill", out var inspectedSkill))
        {
            Add("Skill identity", inspectedSkill.GetProperty("key").GetString()!);
            foreach (var name in new[] { "name", "description", "origin", "projection", "enabled", "revision", "requiredCapabilities", "missingCapabilities" })
                Add("Skill · " + Label(name), Text(inspectedSkill.GetProperty(name)));
            Add("Exact Skill procedure", inspectedSkill.GetProperty("procedure").GetString()!);
        }
        if (data.TryGetProperty("resource", out var inspectedResource))
            foreach (var field in inspectedResource.EnumerateObject()) Add("Resource · " + Label(field.Name), Text(field.Value));
        if (data.TryGetProperty("content", out var resourceText)) Add("Resource text", resourceText.ValueKind == JsonValueKind.Null ? "Not provided" : Text(resourceText));
        Field("truncated", "Resource text is an excerpt"); Field("contentProvided", "Resource text provided");
        if (data.TryGetProperty("eligibleCredentials", out var credentials))
            foreach (var item in credentials.EnumerateArray())
                Add("Readable Credential · " + item.GetProperty("credentialId").GetString(), item.GetProperty("displayName").GetString() + "\nKind: " + item.GetProperty("kind").GetString()
                    + "\nPurpose: " + item.GetProperty("purpose").GetString() + "\nRevision: " + item.GetProperty("revision"));
        if (data.TryGetProperty("blocker", out var blocker) && blocker.ValueKind == JsonValueKind.Object)
            Add("Configuration diagnosis", blocker.GetProperty("reason").GetString()!.Replace('_', ' ') + "\n" + blocker.GetProperty("nextStep").GetString() + "\n" + blocker.GetProperty("adminPath").GetString());
        if (data.TryGetProperty("remediationOptions", out var remedies))
            foreach (var remedy in remedies.EnumerateArray())
                Add("Exact possible remedy · " + remedy.GetProperty("section").GetString(), "Operation: " + remedy.GetProperty("operation").GetString()
                    + "\nTarget: " + remedy.GetProperty("target").GetString() + "\nExpected Agent revision: " + remedy.GetProperty("expectedInstanceRevision")
                    + "\n" + string.Join("\n", remedy.GetProperty("set").EnumerateObject().Select(p => p.Name + ": " + Text(p.Value)))
                    + "\nThis decision reads the option only. A write requires separate current authority and consent.");
        Field("disclosure", "Read boundary"); Field("activation", "Configuration timing"); Field("ownerOnly", "Human setup");
        return result;
    }
}
