using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
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
    public async Task Tenant_resource_namespace_SQLite_shape_and_rollback_keep_exact_member_witnesses()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); var v = await PendingNamespace(f, true); var s = v.Case.Setup;
        foreach (var change in new[] { "grant", "tool", "mixed", "members", "scope", "leaf" })
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.Set<TenantApprovalRecord>().SingleAsync(r => r.RequestId == v.Request.RequestId);
            var data = JsonNode.Parse(row.PayloadJson)!; var read = data["Operation"]!["WorkspaceRead"]!;
            if (change == "grant") row.Kind = 1;
            if (change == "tool") read["ToolName"] = "workspace.read";
            if (change == "mixed") data["Operation"]!["Persona"] = new JsonObject { ["Name"] = "Mixed authority" };
            if (change == "members") read["ResourceMembersJson"] = "{}";
            if (change == "scope") row.ResourceId = "home:" + s.Agent.Instance.InstanceId.ToString("D");
            if (change == "leaf") read["ResourceKey"] = v.Case.Files["a.txt"].Key;
            row.PayloadJson = data.ToJsonString();
            Assert.Equal(275, Assert.IsType<SqliteException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqliteExtendedErrorCode);
        }
        var value = v.Request.Operation.WorkspaceRead!;
        foreach (var members in new[] { new[] { new PreparedResourceReadMember("foreign", 1) },
            new[] { new PreparedResourceReadMember(v.Case.Files["a.txt"].Key, -1) },
            new[] { new PreparedResourceReadMember(v.Case.Files["a.txt"].Key, 1), new PreparedResourceReadMember(v.Case.Files["a.txt"].Key, 1) } })
            Assert.Throws<ArgumentException>(() => TenantApprovalRules.PrepareWorkspaceRead(f.Ids.NewId(), s.Agent.Instance.InstanceId,
                value with { ResourceMembersJson = JsonSerializer.Serialize(members) }, s.Agent.Instance.Revision, s.Agent.Instance.PersonaRevision));
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray(); var index = Array.FindIndex(migrations, id => id.EndsWith("TenantResourceNamespaceReview", StringComparison.Ordinal)); Assert.True(index > 0);
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(migrations[index - 1]));
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
        }
        Assert.Equal(v.Request, await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(s.Tenant, v.Request.RequestId));
    }
}
