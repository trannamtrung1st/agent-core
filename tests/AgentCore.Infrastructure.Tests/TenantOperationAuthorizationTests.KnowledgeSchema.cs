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
    public async Task Tenant_knowledge_SQLite_shape_reopen_and_rollback_preserve_exact_source_metadata()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); var v = await PendingResourceRead(f, knowledge: true); var s = v.Resource.Setup;
        foreach (var change in new[] { "grant", "tool", "mixed", "knowledge", "identity", "path", "members" })
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.Set<TenantApprovalRecord>().SingleAsync(r => r.RequestId == v.Request.RequestId);
            var data = JsonNode.Parse(row.PayloadJson)!; var read = data["Operation"]!["WorkspaceRead"]!;
            if (change == "grant") row.Kind = 1;
            if (change == "tool") read["ToolName"] = "workspace.read";
            if (change == "mixed") data["Operation"]!["Persona"] = new JsonObject { ["Name"] = "Mixed authority" };
            if (change == "knowledge") read["Knowledge"] = null;
            if (change == "identity") read["Knowledge"]!["Identity"] = "different-identity";
            if (change == "path") read["Knowledge"]!["Path"] = "/home/substitute.txt";
            if (change == "members") read["ResourceMembersJson"] = "[]";
            row.PayloadJson = data.ToJsonString();
            Assert.Equal(275, Assert.IsType<SqliteException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqliteExtendedErrorCode);
        }
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray(); var own = Array.FindIndex(migrations, m => m.EndsWith("TenantKnowledgeReadReview", StringComparison.Ordinal)); Assert.True(own > 0);
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(migrations[own - 1]));
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
        }
        var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, v.Request.RequestId));
    }
}
