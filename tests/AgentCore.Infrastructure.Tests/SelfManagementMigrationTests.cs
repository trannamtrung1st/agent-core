using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelfManagement_one_time_normalization_preserves_choices_freeze_history_and_reopens(bool numericEnums)
    {
        await using var f = await Fixture.Create(true, targetMigration: "20261025090000_CanonicalModelIdentity");
        var now = DateTimeOffset.UtcNow;
        var instance = new AgentInstance(Guid.NewGuid(), "examiner", 1, new("Migration", "Test", "", ""), AgentInstanceLifecycle.Active, now, now);
        var legacy = """
            {"policy":{"mode":"Managed","scopes":["KnowledgeResources"],"sources":["retained"],"eligibleTools":[],"frozen":true,"managedAutoApply":[],"builtInSkills":{"configurationAssistance":false,"harnessAuthoring":true}},"policyRevision":7,"preparation":{"purpose":"IMMUTABLE-HISTORY","approvals":[{"actionHash":"EXACT-OLD-HASH"}]},"instanceChanges":null}
            """;
        if (numericEnums) legacy = legacy.Replace("\"Managed\"", "2").Replace("\"KnowledgeResources\"", "0");
        await using (var db = f.Contexts!.CreateDbContext())
        {
            // Seed the actual predecessor columns; today's mapped INSERT includes later fields.
            var persona = JsonSerializer.Serialize(instance.Persona);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentInstances (InstanceId,DefinitionId,ActiveVersion,PersonaJson,Lifecycle,CreatedAtUtc,UpdatedAtUtc,Revision,PersonaRevision,HarnessManagementJson) VALUES ({instance.InstanceId.ToString("D")},{instance.DefinitionId},{instance.ActiveVersion},{persona},{"Active"},{now.ToUnixTimeMilliseconds()},{now.ToUnixTimeMilliseconds()},{instance.Revision},{instance.PersonaRevision},{legacy})");
            await db.Database.MigrateAsync();
        }
        await using var reopened = f.Contexts!.CreateDbContext();
        var row = await reopened.AgentInstances.SingleAsync(x => x.InstanceId == instance.InstanceId.ToString("D"));
        var body = JsonNode.Parse(row.HarnessManagementJson!)!;
        var before = JsonNode.Parse(legacy)!;
        Assert.True(JsonNode.DeepEquals(before["preparation"], body["preparation"]));
        Assert.True(JsonNode.DeepEquals(before["policy"], body["policy"]));
        Assert.Equal(instance.Revision, row.Revision);
        var state = JsonSerializer.Deserialize<HarnessManagementState>(body.ToJsonString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var baseline = (await f.BuiltIns.GetAsync("examiner", 1))! with { SelfManagement = new(HarnessManagementMode.Assisted, [HarnessManagementScope.Settings, HarnessManagementScope.Skills], [], []) };
        var effective = ManagementPolicyResolver.Resolve(baseline, instance with { HarnessManagement = state });
        Assert.Equal(HarnessManagementMode.Managed, effective.Mode);
        Assert.Equal([HarnessManagementScope.KnowledgeResources], effective.Scopes);
        Assert.True(effective.Frozen); Assert.False(effective.BuiltInSkills!.ConfigurationAssistance);
        Assert.Empty(effective.ManagedAutoApply!); Assert.False(effective.Allows(HarnessManagementScope.Settings));
        Assert.False(reopened.Database.HasPendingModelChanges());
        await reopened.Database.MigrateAsync();
        Assert.Equal(row.HarnessManagementJson, (await reopened.AgentInstances.AsNoTracking().SingleAsync(x => x.InstanceId == row.InstanceId)).HarnessManagementJson);
        var error = await Assert.ThrowsAsync<SqliteException>(() => reopened.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentInstances SET HarnessManagementJson={legacy} WHERE InstanceId={row.InstanceId}"));
        Assert.Contains("SelfManagementCanonicalPolicyRequired", error.Message);
    }

    [Theory]
    [InlineData("{\"mode\":{\"value\":\"Unknown\"}}")]
    [InlineData("{\"scopes\":{\"value\":[\"Settings\",\"Settings\"]}}")]
    [InlineData("{\"scopes\":{\"value\":[\"Settings\",3]}}")]
    [InlineData("{\"configurationAssistance\":{\"value\":\"false\"}}")]
    [InlineData("{\"unrecognized\":null}")]
    [InlineData("{\"mode\":{\"value\":\"Managed\",\"extra\":true}}")]
    public async Task SelfManagement_sqlite_rejects_noncanonical_override_documents(string overrides)
    {
        await using var f = await Fixture.Create(true);
        var now = DateTimeOffset.UtcNow;
        var instance = new AgentInstance(Guid.NewGuid(), "examiner", 1, new("Migration", "Test", "", ""), AgentInstanceLifecycle.Active, now, now);
        await f.Instances.InsertAsync(instance);
        var body = JsonSerializer.SerializeToNode(new HarnessManagementState(HarnessManagementPolicy.Disabled, Overrides: new()), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        body["overrides"] = JsonNode.Parse(overrides);
        await using var db = f.Contexts!.CreateDbContext();
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentInstances SET HarnessManagementJson={body.ToJsonString()} WHERE InstanceId={instance.InstanceId.ToString("D")}"));
        Assert.Contains("SelfManagementCanonicalPolicyRequired", error.Message);
        Assert.Null((await f.Instances.FindAsync(instance.InstanceId))!.HarnessManagement);
    }
}
