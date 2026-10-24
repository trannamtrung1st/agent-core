using System.Text;
using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Application.Ports;
using AgentCore.Application.Events;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class BrowserReliabilityTests
{
    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(4096)]
    public void Dense_snapshot_preserves_success_identity_and_discovery_within_remaining_bytes(int budget)
    {
        var json = JsonSerializer.Serialize(new {
            status = "ok", untrustedBrowserContent = true, snapshotId = "snap_test", tabRef = "pg_test",
            url = "https://example.test/catalog", title = "Dense catalog", content = new string('界', 8000),
            truncated = true,
            targets = Enumerable.Range(0, 2000).Select(i => new { target = "el_" + i, role = "button", name = "Open item " + i, actions = new[] { "click" } })
        });
        var fitted = ToolJsonResults.FitToBudget(budget, json);
        Assert.InRange(Encoding.UTF8.GetByteCount(fitted), 1, budget);
        using var result = JsonDocument.Parse(fitted);
        var root = result.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("snap_test", root.GetProperty("snapshotId").GetString());
        Assert.Equal("pg_test", root.GetProperty("tabRef").GetString());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.True(root.GetProperty("hasMore").GetBoolean());
        Assert.Contains("browser.find", root.GetProperty("guidance").GetString());
        Assert.False(root.TryGetProperty("error", out _));
    }

    [Fact]
    public void Budgeted_find_does_not_skip_unreturned_pagination_matches()
    {
        var json = JsonSerializer.Serialize(new { untrustedBrowserContent = true, snapshotId = "snap_find", offset = 10,
            matchCount = 30, returnedCount = 20, nextOffset = 30,
            matches = Enumerable.Range(10, 20).Select(i => new { target = "el_" + i, name = new string('界', 100), role = "button", actions = new[] { "click" } }) });
        var result = ToolJsonResults.FitToBudget(1500, json);
        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        var returned = root.GetProperty("matches").GetArrayLength();
        Assert.InRange(returned, 1, 19);
        Assert.Equal(10 + returned, root.GetProperty("nextOffset").GetInt32());
        Assert.Equal(returned, root.GetProperty("returnedCount").GetInt32());
    }

    [Fact]
    public async Task Nondefault_browser_definition_gets_only_authorized_supported_bootstrap()
    {
        var d = await CapabilityProjectionTests.Definition(ToolCatalog.CapabilitiesLoad, ToolCatalog.BrowserNavigate,
            ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserFind, ToolCatalog.BrowserClose, "browser.console_messages");
        var c = CapabilityProjectionTests.Context(d);
        var projected = ToolProjectionService.Project(d, c, ToolConfigurationGates.AllowAll).Select(t => t.Name).ToArray();
        Assert.Contains(ToolCatalog.BrowserClose, projected);
        Assert.Contains(ToolCatalog.BrowserFind, projected);
        Assert.DoesNotContain(ToolCatalog.BrowserClick, projected);
        Assert.DoesNotContain("browser.console_messages", projected);
        Assert.DoesNotContain(ToolProjectionService.Project(d, c, ToolConfigurationGates.Unconfigured), t => t.Name.StartsWith("browser.", StringComparison.Ordinal));
    }
}
