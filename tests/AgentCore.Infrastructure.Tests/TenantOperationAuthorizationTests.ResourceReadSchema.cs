using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Fact]
    public async Task Tenant_resource_read_SQLite_shape_reopen_and_rollback_preserve_exact_consent()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); var v = await PendingResourceRead(f); var s = v.Resource.Setup;
        var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, v.Request.RequestId));
        foreach (var change in new[] { "key", "working", "tool", "revision", "mixed", "address", "grant", "path" })
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.Set<TenantApprovalRecord>().SingleAsync(r => r.RequestId == v.Request.RequestId);
            var data = JsonNode.Parse(row.PayloadJson)!; var read = data["Operation"]!["WorkspaceRead"]!;
            if (change == "key") read["ResourceKey"] = null;
            if (change == "working") read["Working"] = true;
            if (change == "tool") read["ToolName"] = "workspace.list";
            if (change == "revision") read["ResourceRevision"] = -1;
            if (change == "mixed") data["Operation"]!["Persona"] = new JsonObject { ["Name"] = "Mixed authority" };
            if (change == "address") row.ResourceId = "home:" + s.Agent.Instance.InstanceId.ToString("D");
            if (change == "grant") row.Kind = 1;
            if (change == "path") read["ArgumentsJson"] = """{"path":"/home/substitute.txt"}""";
            row.PayloadJson = data.ToJsonString();
            Assert.Equal(275, Assert.IsType<SqliteException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqliteExtendedErrorCode);
        }
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray(); var index = Array.FindIndex(migrations, id => id.EndsWith("TenantResourceReadReview", StringComparison.Ordinal)); Assert.True(index > 0);
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(migrations[index - 1]));
            Assert.False(db.Database.HasPendingModelChanges());
        }
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, v.Request.RequestId));
    }
}
