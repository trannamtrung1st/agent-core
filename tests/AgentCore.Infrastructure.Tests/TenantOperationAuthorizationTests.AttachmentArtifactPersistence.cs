using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Persistence;
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
    public async Task Tenant_Attachment_export_SQLite_source_shape_and_reopen_prevent_mixed_payloads_and_lossy_downgrade()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true, attachmentAuthority: true); var v = await AttachmentConsent(f); var s = v.Setup;
        var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock, artifactBacking: (SqliteArtifactStore)f.Artifacts, attachmentBacking: (SqliteAttachmentStore)f.Attachments);
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, v.Request.RequestId));
        foreach (var change in new[] { "attachment", "entry", "working", "name", "resource", "path", "size", "mixed" })
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.Set<TenantApprovalRecord>().SingleAsync(r => r.RequestId == v.Request.RequestId); var json = JsonNode.Parse(row.PayloadJson)!;
            var source = json["Operation"]!["ArtifactPublication"]!["Source"]!;
            if (change == "attachment") source["AttachmentId"] = Guid.Empty.ToString("D");
            if (change == "entry") source["EntryId"] = new string('z', 36);
            if (change == "name") source["AttachmentName"] = null;
            if (change == "working") source["Working"] = false;
            if (change == "resource") { source["ResourceKey"] = "instance:" + Guid.NewGuid().ToString("D"); source["ResourceRevision"] = 1; }
            if (change == "path") source["Path"] = "/attachments/" + Guid.NewGuid().ToString("D");
            if (change == "size") source["ByteSize"] = 26214401;
            if (change == "mixed") json["Operation"]!["AttachmentRead"] = new JsonObject { ["SessionId"] = v.Frame.SessionId.ToString("D") };
            row.PayloadJson = json.ToJsonString();
            Assert.Equal(275, Assert.IsType<SqliteException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqliteExtendedErrorCode);
        }
        await using (var db = f.Contexts!.CreateDbContext()) await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261019114545_TenantAttachmentReadReview"));
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, v.Request.RequestId));
        await using (var db = f.Contexts!.CreateDbContext()) { await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges()); }
    }
}
