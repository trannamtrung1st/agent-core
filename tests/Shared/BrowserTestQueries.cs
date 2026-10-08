using System.Text.Json;
using AgentCore.Application.Ports;
namespace AgentCore.Tests.Shared;

public static class BrowserTestQueries
{
    // Fixture setup uses bounded semantic queries, never native ARIA parsing or a DOM index.
    public static async Task<BrowserElement> Find(IBrowser browser, Guid id, string name, string? role = null)
    {
        var queries = role is not null ? new[] { new BrowserTargetQuery(Role: role, Name: name) }
            : new[] { new BrowserTargetQuery(Label: name), new BrowserTargetQuery(Role: "button", Name: name),
                new BrowserTargetQuery(Role: "link", Name: name), new BrowserTargetQuery(Role: "treeitem", Name: name),
                new BrowserTargetQuery(Role: "textbox", Name: name), new BrowserTargetQuery(Role: "combobox", Name: name),
                new BrowserTargetQuery(Role: "checkbox", Name: name), new BrowserTargetQuery(Role: "radio", Name: name),
                new BrowserTargetQuery(Role: "tree", Name: name), new BrowserTargetQuery(Role: "grid", Name: name),
                new BrowserTargetQuery(Role: "group", Name: name), new BrowserTargetQuery(Role: "region", Name: name),
                new BrowserTargetQuery(Role: "heading", Name: name), new BrowserTargetQuery(Text: name), new BrowserTargetQuery(AltText: name), new BrowserTargetQuery(Title: name) };
        foreach (var query in queries)
        {
            var result = await browser.ExecuteAsync(new(id, BrowserOperation.Find, new() { Query = query }));
            if (result.ErrorCode == "not_found") continue;
            if (result.ErrorCode is not null) throw new InvalidOperationException($"Fixture search failed: {result.ErrorCode} for {name}");
            using var doc = JsonDocument.Parse(result.DataJson!);
            var match = doc.RootElement.GetProperty("matches")[0];
            var state = match.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<BrowserControlState>(st, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
            return new(match.GetProperty("ref").GetString()!, match.GetProperty("role").GetString()!, match.GetProperty("name").GetString()!,
                match.GetProperty("actions").EnumerateArray().Select(a=>a.GetString()!).ToArray(), state);
        }
        throw new InvalidOperationException($"Fixture target missing: {name}");
    }
}

public static class BrowserFixtureResults
{
    public static BrowserResult Find(BrowserRequest request, BrowserSnapshot snapshot)
    {
        var q = request.Options.Query!;
        var name = q.Name ?? q.Label ?? q.Text ?? q.Placeholder ?? q.Title ?? q.AltText;
        var matches = snapshot.Targets.Where(e => (name is null || e.Name == name) && (q.Role is null || e.Role == q.Role)).ToArray();
        if (matches.Length == 0) return new("not_found");
        if (matches.Length != 1) return new("ambiguous_target");
        return new(null, DataJson: JsonSerializer.Serialize(new { status = "ok", untrustedBrowserContent = true,
            matches = matches.Select(e => new { @ref = e.Ref, role = e.Role, name = e.Name, actions = e.Actions, state = e.State }), returnedCount = 1 }));
    }
}
