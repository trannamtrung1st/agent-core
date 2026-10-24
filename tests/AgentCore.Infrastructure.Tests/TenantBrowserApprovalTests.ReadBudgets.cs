using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    [Theory]
    [InlineData(false, 512)] [InlineData(true, 512)]
    [InlineData(false, 1024)] [InlineData(true, 1024)]
    [InlineData(false, 4096)] [InlineData(true, 4096)]
    public async Task Tenant_Browser_dense_read_keeps_identity_when_it_fits_and_body_free_facts_below_metadata_floor(bool sqlite, int budget)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, permissions: false);
        var snapshot = new BrowserSnapshot("https://example.test/", "Dense", new string('界', 3000), true,
            Enumerable.Range(0, 2000).Select(i => new BrowserElement(new BrowserTarget("role", "button", Name: "Entry " + i), "button", "Entry " + i, ["click"])).ToArray(),
            SnapshotId: "snap_dense", TabRef: "pg_dense");
        var probe = new ReadProbe(Probe(s)) { Observation = snapshot };
        var text = await ReadService(f, probe).InspectAsync(Frame(s), ReadCall(), budget);
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, budget);
        using var result = JsonDocument.Parse(text); var data = result.RootElement;
        Assert.Equal("ok", data.GetProperty("status").GetString());
        if (budget == 512)
        {
            // Current tenant scope/hash metadata cannot fit this envelope. Preserve
            // only factual no-effect status, rather than a partial private observation.
            Assert.Equal(new[] { "applicationOutcomeVerified", "effectAttempted", "effectConfirmedBySdk", "status", "truncated" },
                data.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.True(data.GetProperty("truncated").GetBoolean());
            Assert.False(data.GetProperty("effectAttempted").GetBoolean()); Assert.False(data.GetProperty("effectConfirmedBySdk").GetBoolean());
            Assert.False(data.GetProperty("applicationOutcomeVerified").GetBoolean());
        }
        else
        {
            Assert.Equal("snap_dense", data.GetProperty("snapshotId").GetString()); Assert.Equal("pg_dense", data.GetProperty("tabRef").GetString());
            Assert.True(data.GetProperty("hasMore").GetBoolean()); Assert.True(data.GetProperty("tenantBrowserRead").GetBoolean());
            Assert.Equal(64, data.GetProperty("readCommandHash").GetString()!.Length);
        }
        Assert.False(data.TryGetProperty("error", out _)); Assert.DoesNotContain('\uFFFD', text);
        Assert.Equal(2000, snapshot.Targets.Count); Assert.Equal(1, probe.Reads);
        Assert.Equal(AgentRunSideEffectDisposition.None, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!.SideEffect.Disposition);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_scoped_table_retains_native_values_that_fit_without_splitting_UTF8(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, permissions: false);
        var rows = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"    - row \"Sensor {i} 界 value {i * 10} Good\":\n      - cell \"Sensor {i}\"\n      - cell \"{i * 10}\""));
        var probe = new ReadProbe(Probe(s)) { Observation = new("https://example.test/asset", "Asset", "- table:\n" + rows, false, [], SnapshotId: "snap_table", TabRef: "pg_table") };
        const int budget = 2600;
        var text = await ReadService(f, probe).InspectAsync(Frame(s), ReadCall() with { ArgumentsJson = "{\"target\":{\"by\":\"role\",\"value\":\"table\"}}" }, budget);
        using var result = JsonDocument.Parse(text); var data = result.RootElement;
        Assert.Contains("Sensor 10", data.GetProperty("content").GetString()); Assert.DoesNotContain('\uFFFD', text);
        Assert.True(data.GetProperty("hasMore").GetBoolean()); Assert.InRange(Encoding.UTF8.GetByteCount(text), budget - 32, budget);
        Assert.Equal(1, probe.Reads);
    }
}
