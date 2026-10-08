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
            targets = Enumerable.Range(0, 2000).Select(i => new { @ref = "el_" + i, role = "button", name = "Open item " + i, actions = new[] { "click" } })
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

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(4096)]
    public async Task Executor_projects_successful_provider_snapshot_without_losing_index(int budget)
    {
        var browser = new DenseBrowser();
        var definition = await CapabilityProjectionTests.Definition(ToolCatalog.BrowserSnapshot);
        var executor = new SessionToolExecutor(browser: browser, configurationGate: ToolConfigurationGates.AllowAll);
        var result = await executor.ExecuteAsync(definition, Guid.NewGuid(), new("snapshot", ToolCatalog.BrowserSnapshot, "{}"), budget,
            admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn));
        Assert.InRange(Encoding.UTF8.GetByteCount(result.Text), 1, budget);
        using var json = JsonDocument.Parse(result.Text);
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("snap_dense", json.RootElement.GetProperty("snapshotId").GetString());
        Assert.True(json.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("error", out _));
        Assert.Equal(2000, browser.Snapshot.Targets.Count);
    }

    [Fact]
    public void Budgeted_find_does_not_skip_unreturned_pagination_matches()
    {
        var json = JsonSerializer.Serialize(new { untrustedBrowserContent = true, snapshotId = "snap_find", offset = 10,
            matchCount = 30, returnedCount = 20, nextOffset = 30,
            matches = Enumerable.Range(10, 20).Select(i => new { @ref = "el_" + i, name = new string('界', 100), role = "button", actions = new[] { "click" } }) });
        var result = ToolJsonResults.FitToBudget(1500, json);
        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        var returned = root.GetProperty("matches").GetArrayLength();
        Assert.InRange(returned, 1, 19);
        Assert.Equal(10 + returned, root.GetProperty("nextOffset").GetInt32());
        Assert.Equal(returned, root.GetProperty("returnedCount").GetInt32());
    }

    private sealed class DenseBrowser : IBrowser
    {
        public BrowserSnapshot Snapshot { get; } = new("https://example.test/", "Dense", new string('界', 3000), true,
            Enumerable.Range(0, 2000).Select(i => new BrowserElement("el_" + i, "button", "Entry " + i, ["click"])).ToArray(),
            SnapshotId: "snap_dense", TabRef: "pg_dense");
        public bool IsAvailable => true;
        public BrowserProviderDescriptor Provider { get; } = new("dense", "Dense", new HashSet<BrowserFeature> { BrowserFeature.Snapshot });
        public BrowserHostPolicy HostPolicy { get; } = new(true, true, BrowserInteractionMode.InteractiveDemo, ["https://example.test"]);
        public ValueTask<Uri?> GetCurrentUrlAsync(Guid id, CancellationToken ct = default) => new(new Uri("https://example.test/"));
        public ValueTask<BrowserResult> NavigateAsync(BrowserRequest request, CancellationToken ct = default) => new(new BrowserResult(null, Snapshot));
        public ValueTask<BrowserResult> SnapshotAsync(Guid id, CancellationToken ct = default) => new(new BrowserResult(null, Snapshot));
        public ValueTask<BrowserResult> InteractAsync(BrowserRequest request, CancellationToken ct = default) => new(new BrowserResult(null, Snapshot));

        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default) => request.Operation switch
        {
            BrowserOperation.Navigate => NavigateAsync(request, ct),
            BrowserOperation.Snapshot or BrowserOperation.WaitFor => SnapshotAsync(request.SessionId, ct),
            BrowserOperation.Click or BrowserOperation.Type or BrowserOperation.Hover or BrowserOperation.Drag or BrowserOperation.Upload or BrowserOperation.FillForm => InteractAsync(request, ct),
            _ => new(new BrowserResult("unsupported_operation")),
        };
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
