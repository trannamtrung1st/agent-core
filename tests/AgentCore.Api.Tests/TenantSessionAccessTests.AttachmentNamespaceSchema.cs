using System.Text.Json.Nodes;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Fact]
    public async Task Tenant_attachment_namespace_SQLite_shape_and_failed_narrowing_preserve_saved_exact_consent()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-namespace-schema-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new AttachmentReadModel { ReadTool = ToolCatalog.WorkspaceSearch, ReviewRead = true };
            await using var factory = new NativeTenantSqliteFactory(root, model); using var owner = factory.CreateClient();
            var value = await PrepareAttachmentRead(factory.Services, owner, "text", virtualTool: ToolCatalog.WorkspaceSearch); var v = value.Execution;
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            Assert.Equal(AgentCore.Domain.Conversation.AgentRunStatus.Completed, (await AttachmentTerminal(runs, v.Tenant, await SendNamespaceMessage(owner, v, factory.Services, [v.Id]))).Status);
            var policy = await owner.PutAsJsonAsync($"/api/v2/tenants/{v.Tenant}/operation-policies/attachments.read", new { mode = "always", independentReview = false,
                expectedRevision = await Revision(owner, v.Tenant), expectedPolicyRevision = 0 }); Assert.True(policy.IsSuccessStatusCode);
            model.ReadEnabled = true; model.Arguments = """{"path":"/attachments","query":"body"}""";
            var request = await AwaitAttachmentReview(factory.Services, v.Tenant, await SendNamespaceMessage(owner, v, factory.Services));
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>();
            foreach (var change in new[] { "kind", "permission", "scope", "working", "tool", "path", "members", "mixed", "resource", "knowledge" })
            {
                await using var db = await contexts.CreateDbContextAsync(); var row = await db.Set<TenantApprovalRecord>().SingleAsync(r => r.RequestId == request.RequestId);
                var data = JsonNode.Parse(row.PayloadJson)!; var read = data["Operation"]!["WorkspaceRead"]!;
                if (change == "kind") row.Kind = 1;
                if (change == "permission") row.Permission = "workspaces.read";
                if (change == "scope") row.ResourceId = "home:" + value.Instance.ToString("D");
                if (change == "working") read["Working"] = true;
                if (change == "tool") read["ToolName"] = ToolCatalog.WorkspaceRead;
                if (change == "path") read["ArgumentsJson"] = """{"path":"/home","query":"body"}""";
                if (change == "members") read["AttachmentMembersJson"] = "{}";
                if (change == "mixed") data["Operation"]!["Persona"] = new JsonObject { ["Name"] = "Mixed authority" };
                if (change == "resource") read["ResourceMembersJson"] = "[]";
                if (change == "knowledge") read["Knowledge"] = new JsonObject { ["Identity"] = "foreign" };
                row.PayloadJson = data.ToJsonString();
                var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); Assert.Equal(275, Assert.IsType<SqliteException>(error.InnerException).SqliteExtendedErrorCode);
            }
            await using (var db = await contexts.CreateDbContextAsync())
            {
                var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray(); var own = Array.FindIndex(migrations, m => m.EndsWith("TenantAttachmentNamespaceReview", StringComparison.Ordinal)); Assert.True(own > 0);
                await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(migrations[own - 1])); Assert.False(db.Database.HasPendingModelChanges());
            }
            Assert.Equal(request, await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(v.Tenant, request.RequestId));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
