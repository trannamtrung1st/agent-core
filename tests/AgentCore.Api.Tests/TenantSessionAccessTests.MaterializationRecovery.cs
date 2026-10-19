using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Workspaces;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Fact]
    public async Task Human_materialization_actual_SQLite_host_recreation_preserves_provenance_independent_copy_and_deleted_owner_cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-materialize-reopen-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            MaterializationSetup v; Guid id; ArtifactRecord before;
            await using (var first = new NativeTenantSqliteFactory(root, new ApprovalRecordingModel()))
            {
                using var owner = first.CreateClient(); v = await PrepareMaterialization(owner, root);
                var result = await Json(await owner.PostAsJsonAsync(v.Url, new { expectedRevision = await Revision(owner, v.Tenant) })); id = result.GetProperty("artifactId").GetGuid();
                before = (await first.Services.GetRequiredService<ITenantArtifactStore>().FindTenantAsync(v.Tenant, v.Session, id))!;
                Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(v.SessionUrl + $"/attachments/{v.Attachment}?requestId={Guid.NewGuid()}&expectedRevision={await Revision(owner, v.Tenant)}&expectedSessionRevision=1")).StatusCode);
                await first.Services.GetRequiredService<TenantAttachmentCleanupService>().RecoverAsync();
                Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(v.SessionUrl + $"/attachments/{v.Attachment}/content")).StatusCode);
                Assert.Equal(v.Bytes, await owner.GetByteArrayAsync(v.SessionUrl + $"/artifacts/{id}/content"));
            }
            await using (var second = new NativeTenantSqliteFactory(root, new ApprovalRecordingModel()))
            {
                using var owner = second.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                Assert.Equal(before, await second.Services.GetRequiredService<ITenantArtifactStore>().FindTenantAsync(v.Tenant, v.Session, id));
                Assert.Equal(v.Bytes, await owner.GetByteArrayAsync(v.SessionUrl + $"/artifacts/{id}/content"));
                Assert.Equal(v.Bytes, await owner.GetByteArrayAsync(v.SessionUrl + "/workspace/content?path=%2Fworking%2Fsource.txt"));
                Assert.Single((await Json(await owner.GetAsync(v.SessionUrl + "/artifacts"))).GetProperty("items").EnumerateArray());
                var snapshot = await Json(await owner.GetAsync(v.SessionUrl));
                Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(v.SessionUrl + $"?expectedRevision={await Revision(owner, v.Tenant)}&expectedSessionRevision={snapshot.GetProperty("revision").GetInt64()}")).StatusCode);
            }
            await using (var third = new NativeTenantSqliteFactory(root, new ApprovalRecordingModel()))
            {
                using var owner = third.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                await third.Services.GetRequiredService<TenantArtifactCleanupService>().RecoverAsync();
                Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(v.SessionUrl + $"/artifacts/{id}/content")).StatusCode);
                Assert.False(Directory.Exists(Path.Combine(root, "artifacts", "tenants", v.Tenant.ToString("N"), v.Session.ToString("N"))));
                await using var db = await third.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                var row = await db.Artifacts.AsNoTracking().SingleAsync(a => a.ArtifactId == id.ToString("D")); Assert.Equal(v.Actor, row.CreatedByPrincipalId); Assert.Null(row.AgentRunId);
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Human_materialization_SQLite_rejects_forged_or_mixed_provenance_immutable_updates_and_lossy_downgrade()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-materialize-schema-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var factory = new NativeTenantSqliteFactory(root, new ApprovalRecordingModel()); using var owner = factory.CreateClient(); var v = await PrepareMaterialization(owner, root);
            var result = await Json(await owner.PostAsJsonAsync(v.Url, new { expectedRevision = await Revision(owner, v.Tenant) })); var id = result.GetProperty("artifactId").GetGuid();
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>();
            ArtifactRecordRow original;
            await using (var db = await contexts.CreateDbContextAsync()) original = await db.Artifacts.AsNoTracking().SingleAsync(a => a.ArtifactId == id.ToString("D"));
            foreach (var boundary in new[] { "human", "source", "path", "mixed", "hash", "runless", "tenant" })
            {
                await using var db = await contexts.CreateDbContextAsync(); var row = JsonSerializer.Deserialize<ArtifactRecordRow>(JsonSerializer.Serialize(original))!;
                var next = Guid.NewGuid(); row.ArtifactId = next.ToString("D"); row.BlobKey = $"tenants/{v.Tenant:N}/{v.Session:N}/{next:N}";
                if (boundary == "human") row.CreatedByPrincipalId = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!.Owner.ExecutorPrincipalId;
                if (boundary == "source") row.SourceAttachmentId = Guid.NewGuid().ToString("D");
                if (boundary == "path") row.WorkspaceLogicalPath = "/home/source.txt";
                if (boundary == "mixed") row.AgentRunId = Guid.NewGuid().ToString("D");
                if (boundary == "hash") row.Sha256Hex = new string('0', 64);
                if (boundary == "runless") row.CreatedByPrincipalId = null;
                if (boundary == "tenant") row.TenantId = Guid.NewGuid();
                db.Artifacts.Add(row); var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); Assert.IsType<SqliteException>(error.InnerException);
            }
            await using (var db = await contexts.CreateDbContextAsync())
            {
                var row = await db.Artifacts.SingleAsync(a => a.ArtifactId == id.ToString("D")); row.CreatedByPrincipalId = Guid.NewGuid();
                var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); Assert.Equal(1811, Assert.IsType<SqliteException>(error.InnerException).SqliteExtendedErrorCode);
            }
            await using (var db = await contexts.CreateDbContextAsync())
            {
                var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray(); var index = Array.FindIndex(migrations, s => s.EndsWith("TenantHumanMaterializedArtifact", StringComparison.Ordinal)); Assert.True(index > 0);
                await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(migrations[index - 1])); Assert.False(db.Database.HasPendingModelChanges());
            }
            Assert.Equal(v.Bytes, await owner.GetByteArrayAsync(v.SessionUrl + $"/artifacts/{id}/content"));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Human_materialization_skips_occupied_symlinks_without_overwrite_and_refuses_exhausted_names_and_stale_witness(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-materialize-names-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel();
            await using Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); var v = await PrepareMaterialization(owner, root);
            var stale = await Revision(owner, v.Tenant);
            await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other materialization tenant", slug = "other-copy-tenant" }));
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(v.Url, new { expectedRevision = stale - 1 })).StatusCode);
            Assert.False(Directory.Exists(v.Physical)); Directory.CreateDirectory(v.Physical);
            var external = Path.Combine(root, "never-overwrite.txt"); await File.WriteAllTextAsync(external, "outside");
            File.CreateSymbolicLink(Path.Combine(v.Physical, "source.txt"), external); Directory.CreateDirectory(Path.Combine(v.Physical, "source-2.txt"));
            var result = await Json(await owner.PostAsJsonAsync(v.Url, new { expectedRevision = await Revision(owner, v.Tenant) }));
            Assert.Equal("/working/source-3.txt", result.GetProperty("workspaceLogicalPath").GetString()); Assert.Equal("outside", await File.ReadAllTextAsync(external));
            for (var copy = 4; copy <= 128; copy++) await File.WriteAllTextAsync(Path.Combine(v.Physical, $"source-{copy}.txt"), "occupied");
            var rejected = await owner.PostAsJsonAsync(v.Url, new { expectedRevision = await Revision(owner, v.Tenant) }); Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Equal("workspace_name_conflict", (await Json(rejected)).GetProperty("reasonCode").GetString());
            Assert.Single((await Json(await owner.GetAsync(v.SessionUrl + "/artifacts"))).GetProperty("items").EnumerateArray());
            Assert.Equal(v.Bytes, await owner.GetByteArrayAsync(v.SessionUrl + $"/attachments/{v.Attachment}/content")); Assert.Empty(model.Requests);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
