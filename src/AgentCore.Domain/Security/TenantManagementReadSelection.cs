using System.Text.Json;
namespace AgentCore.Domain.Security;

// Diagnosis wording is not a second read intent. Exact saved actions still retain the full arguments.
public static class TenantManagementReadSelection
{
    public static string? Target(JsonElement args)
    {
        if (args.TryGetProperty("target", out var target)) return target.GetString();
        if (!args.TryGetProperty("goal", out var goal)) return null;
        var text = goal.GetString()!.ToLowerInvariant();
        return text.Contains("email") ? "email.send" : text.Contains("sign in") ? "browser.fill_credential"
            : text.Contains("browser") || text.Contains("look up") ? "browser.navigate"
            : text.Contains("brief") || text.Contains("concise") ? "settings.patch"
            : text.Contains("reminder") || text.Contains("automation") ? "automation.create" : null;
    }
    public static string? Target(string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return Target(document.RootElement);
    }
    public static string SemanticArguments(string tool, string arguments)
    {
        if (tool != "configuration.inspect") return arguments;
        using var document = JsonDocument.Parse(arguments); var args = document.RootElement;
        return args.EnumerateObject().Any() ? JsonSerializer.Serialize(new { target = Target(args) }) : "{}";
    }
}
