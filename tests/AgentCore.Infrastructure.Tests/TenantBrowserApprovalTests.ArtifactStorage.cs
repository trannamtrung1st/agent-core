using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    [Fact]
    public async Task Tenant_Browser_download_SQLite_failed_second_blob_keeps_all_metadata_unpublished_and_factual_receipt_completed()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); await GrantBrowserPublication(f, s);
        var probe = new EffectProbe(Probe(s)) { Result = new(null, Downloads: [new(null, "one.txt", "text/plain", [1]), new(null, "two.txt", "text/plain", [2])],
            EffectAttempted: true, EffectConfirmedBySdk: true) };
        (s, var request) = await ApprovedEffect(f, s, probe, downloads: true);
        var path = Path.Combine(f.Root, "artifacts", "tenants", s.Tenant.ToString("N"), s.Run.SessionId.ToString("N")); Directory.CreateDirectory(path);
        var outside = Path.Combine(f.Root, "outside-publication.txt"); await File.WriteAllTextAsync(outside, "preserve neighbor");
        File.CreateSymbolicLink(Path.Combine(path, TenantBrowserArtifactRules.Id(request.Operation.OperationId, "download", 1).ToString("N")), outside);
        var result = await ExecuteEffect(Effects(f, probe), s, request);
        Assert.Equal(TenantOperationOutcome.Completed, result.Receipt.Outcome);
        Assert.Equal("unavailable", TenantBrowserArtifactRules.Read(result.Receipt.BrowserArtifactsJson!).Status);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 100)).Items);
        Assert.Equal("preserve neighbor", await File.ReadAllTextAsync(outside));
        Assert.Equal(result.Receipt, (await ExecuteEffect(Effects(f, probe), s, request)).Receipt); Assert.Equal(1, probe.NativeEffects);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_file_batch_quota_refuses_all_members_before_any_metadata_or_blob(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); var frame = new TenantRuntimeFrame(s.Run.Owner, s.Run.SessionId, s.Run.AgentRunId, s.Run.Claim!.Generation);
        var files = new TenantBrowserArtifactFile[] { new("download", f.Ids.NewId(), "one.txt", "text/plain", new byte[] { 1, 2 }), new("download", f.Ids.NewId(), "two.txt", "text/plain", new byte[] { 3, 4 }) };
        if (sqlite)
        {
            var path = Path.Combine(f.Root, "small-artifacts"); var store = new SqliteArtifactStore(f.Contexts!, f.Clock, path, maxBytesSession: 3);
            await using var db = await f.Contexts!.CreateDbContextAsync(); await using var tx = await db.Database.BeginTransactionAsync();
            await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => store.RequireBrowserBatchCapacityAsync(db, frame, files, default).AsTask());
            Assert.False(Directory.Exists(Path.Combine(path, "tenants")));
        }
        else
        {
            var store = new InMemoryArtifactStore(f.Clock, maxBytesSession: 3).BindTenantAuthority((InMemoryMemoryStore)f.Memory);
            Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() => store.PublishBrowserBatchLocal(frame, files, f.Clock.GetUtcNow(), default));
            Assert.Empty((await store.ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 100)).Items);
        }
    }
    [Fact]
    public async Task Tenant_Browser_capture_SQLite_guards_reject_forged_publication_and_downgrade_without_losing_saved_facts()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); await GrantBrowserPublication(f, s);
        var probe = new EffectProbe(Probe(s)) { Result = new(null, Bytes: CapturePng, ContentType: "image/png") };
        (s, var request) = await ApprovedEffect(f, s, probe, tool: AgentCore.Application.Tools.ToolCatalog.BrowserScreenshot, args: "{}");
        var result = await ExecuteEffect(Effects(f, probe), s, request);
        await using var db = await f.Contexts!.CreateDbContextAsync();
        var bad = result.Receipt with { OperationId = f.Ids.NewId(), BrowserArtifactsJson = "{\"status\":\"published\",\"reasonCode\":null,\"files\":[{\"kind\":\"capture\",\"artifactId\":\"" + f.Ids.NewId().ToString("D") + "\"}]}" };
        db.Add(TenantOperationReceiptRecord.From(bad));
        var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("actual owned publication", rejected.InnerException!.Message); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantOperationReceipts SET BrowserArtifactsJson = NULL WHERE OperationId = {request.Operation.OperationId}"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261019200000_TenantBrowserUploadSources"));
        await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(result.Receipt, await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId));
        Assert.Equal(CapturePng, await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, s.Run.SessionId, request.Operation.OperationId));
    }
}
