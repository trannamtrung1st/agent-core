using System.Text.Json;
using AgentCore.Application.Ports;
namespace AgentCore.Tests.Shared;

public static class BrowserTestQueries
{
    public static BrowserScope Scope(BrowserTarget target) => new(target.By, target.Value, target.Name, target.Exact, target.Visible, target.HasText);
    public static BrowserTarget ReadTarget(JsonElement match) => JsonSerializer.Deserialize<BrowserTarget>(match.GetProperty("target"), JsonSerializerOptions.Web)!;
    public static async Task<BrowserElement> Find(IBrowser browser, Guid id, string name, string? role = null)
    {
        var queries = role is not null ? new[] { new BrowserTarget("role", role, Name: name) }
            : new[] { new BrowserTarget("label", name) }.Concat(new[] { "button", "link", "treeitem", "textbox", "combobox", "checkbox", "radio", "tree", "grid", "group", "region", "heading" }
                .Select(r => new BrowserTarget("role", r, Name: name))).Concat(new[] { "text", "altText", "title" }.Select(by => new BrowserTarget(by, name))).ToArray();
        foreach (var query in queries)
        {
            var result = await browser.ExecuteAsync(new(id, new BrowserFind(query)));
            if (result.ErrorCode == "not_found") continue;
            if (result.ErrorCode is not null) throw new InvalidOperationException($"Fixture search failed: {result.ErrorCode} for {name}");
            using var doc = JsonDocument.Parse(result.DataJson!);
            var match = doc.RootElement.GetProperty("matches")[0];
            var state = match.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<BrowserControlState>(st, JsonSerializerOptions.Web) : null;
            return new(ReadTarget(match), match.GetProperty("role").GetString()!, match.GetProperty("name").GetString()!,
                match.GetProperty("actions").EnumerateArray().Select(a=>a.GetString()!).ToArray(), state);
        }
        throw new InvalidOperationException($"Fixture target missing: {name}");
    }
}

public static class BrowserFixtureResults
{
    public static BrowserResult Find(BrowserRequest request, BrowserSnapshot snapshot)
    {
        var q = ((BrowserFind)request.Command).Target;
        var matches = snapshot.Targets.Where(e => (q.By == "role" ? e.Role == q.Value && (q.Name is null || e.Name == q.Name) : e.Name == q.Value)).ToArray();
        if (matches.Length == 0) return new("not_found");
        if (matches.Length != 1) return new("ambiguous_target");
        return new(null, DataJson: JsonSerializer.Serialize(new { status = "ok", untrustedBrowserContent = true,
            matches = matches.Select(e => new { target = e.Target, role = e.Role, name = e.Name, actions = e.Actions, state = e.State }), returnedCount = 1 }, JsonSerializerOptions.Web));
    }
}
