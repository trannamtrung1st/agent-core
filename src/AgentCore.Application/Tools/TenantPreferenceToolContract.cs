using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed record TenantPreferencePatch(string CanonicalArguments, string ParametersHash,
    IReadOnlyDictionary<string, JsonElement> Set, IReadOnlyList<string> Clear);

public static class TenantPreferenceToolContract
{
    public const string Name = "settings.update_preferences";
    public const string Permission = "agents.update";
    public static ToolDescriptor Descriptor() => new(new ModelToolDefinition(Name,
        "Update or restore only this agent's conversation response length, language and one-question preference. Core derives the target and current authority. Requires pinned and current Managed auto-apply eligibility and an Allowed IAM/review decision. Results are durable per exact Run/call; no approval or permission is implied for another action.",
        """{"type":"object","additionalProperties":false,"properties":{"set":{"type":"object","additionalProperties":false,"properties":{"responseLength":{"type":"string","enum":["concise","balanced"]},"language":{"type":"string","minLength":1,"maxLength":64},"askOneQuestionAtATime":{"type":"boolean"}}},"clear":{"type":"array","uniqueItems":true,"maxItems":3,"items":{"type":"string","enum":["responseLength","language","askOneQuestionAtATime"]}}},"required":["set","clear"]}"""),
        ToolEffect.Write, ToolOfferRule.TenantExecutionAuthority, ToolResourceScope.Session);

    public static TenantPreferencePatch Parse(string arguments)
    {
        if (Encoding.UTF8.GetByteCount(arguments) > 4096) throw new ArgumentException("Preference arguments exceed the bound.");
        using var doc = JsonDocument.Parse(arguments); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
            || !root.TryGetProperty("set", out var set) || set.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("clear", out var clear) || clear.ValueKind != JsonValueKind.Array) throw new ArgumentException("Use exact set/clear fields.");
        var fields = new[] { "responseLength", "language", "askOneQuestionAtATime" };
        var values = set.EnumerateObject().ToArray(); var removed = clear.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new ArgumentException("Invalid clear.")).ToArray();
        if (values.Length + removed.Length is < 1 or > 3 || values.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != values.Length
            || removed.Distinct(StringComparer.Ordinal).Count() != removed.Length || values.Any(p => !fields.Contains(p.Name, StringComparer.Ordinal) || removed.Contains(p.Name, StringComparer.Ordinal))
            || removed.Any(p => !fields.Contains(p, StringComparer.Ordinal))) throw new ArgumentException("Only independent conversation preference fields are supported.");
        foreach (var field in values)
            if (field.Name == "askOneQuestionAtATime" ? field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                : field.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.Value.GetString()) || field.Value.GetString()!.Length > 64
                    || field.Name == "responseLength" && field.Value.GetString() is not ("concise" or "balanced")) throw new ArgumentException("Invalid preference value.");
        var copy = values.OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        var sorted = removed.Order(StringComparer.Ordinal).ToArray();
        var canonical = JsonSerializer.Serialize(new { set = copy, clear = sorted });
        return new(canonical, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Name + "\n" + canonical))), copy, sorted);
    }
    public static bool Allows(AgentDefinition definition) => definition.SelfManagement?.Allows(HarnessManagementScope.Settings) == true
        && definition.SelfManagement.AutoApplies(ManagementAutoApply.ConversationPreferences);
    public static string ActionHash(Guid instance, long revision, string parametersHash) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { operation = Name, permission = Permission, instance, revision, parametersHash }))));
}
