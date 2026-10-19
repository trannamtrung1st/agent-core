using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Fact]
    public async Task Tenant_attachment_migration_preserves_legacy_rows_without_adoption_and_can_narrow_when_no_owned_rows_exist()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, attachmentAuthority: true);
        var id = Guid.NewGuid().ToString("D"); var session = Guid.NewGuid().ToString("D");
        await using var db = f.Contexts!.CreateDbContext(); var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var own = Array.FindIndex(migrations, m => m.EndsWith("TenantAttachmentOwnership", StringComparison.Ordinal)); Assert.True(own > 0);
        await db.GetService<IMigrator>().MigrateAsync(migrations[own - 1]);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Attachments (AttachmentId,SessionId,BlobKey,DisplayName,ContentType,ByteSize,Sha256Hex,State,Readable,StageForNextTurn,CreatedAtUtc,ExpiresAtUtc) VALUES ({id},{session},'legacy/source','legacy.txt','text/plain',3,{new string('a',64)},'Pending',1,0,1,3600001)");
        await db.GetService<IMigrator>().MigrateAsync();
        var saved = await db.Attachments.AsNoTracking().SingleAsync(); Assert.Equal(id, saved.AttachmentId); Assert.Equal(session, saved.SessionId);
        Assert.Equal("legacy/source", saved.BlobKey); Assert.Equal(3, saved.ByteSize); Assert.Null(saved.TenantId); Assert.Null(saved.UploadRequestId); Assert.Null(saved.UploadedByPrincipalId);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Tenant_attachment_SQLite_shape_immutability_scope_and_failed_narrowing_preserve_owned_data()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, attachmentAuthority: true); var s = await Setup(f, "general-assistant", 32); var id = await UploadAttachment(f, s);
        var files = (ITenantAttachmentStore)f.Attachments; var original = (await files.FindTenantAsync(s.Tenant, s.Run.SessionId, id))!;
        var other = await f.Sessions.CreateAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Owner, SessionMode.Text, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Agent.Instance.Revision);
        await f.RunService().SubmitTextAsync(s.Tenant, other.Owner.SessionId, s.Owner, f.Ids.NewId(), "Foreign entry", (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        string foreignEntry; await using (var db = f.Contexts!.CreateDbContext()) foreignEntry = (await db.Entries.SingleAsync(e => e.SessionId == other.Owner.SessionId.ToString("D"))).EntryId;
        foreach (var change in new[] { "tenant", "session", "actor", "hash", "path", "request", "expiry", "entry", "shadow" })
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.Attachments.SingleAsync(a => a.AttachmentId == id.ToString("D"));
            if (change == "tenant") row.TenantId = Guid.NewGuid();
            if (change == "session") row.SessionId = other.Owner.SessionId.ToString("D");
            if (change == "actor") row.UploadedByPrincipalId = s.Agent.PrincipalId;
            if (change == "hash") row.Sha256Hex = new string('a', 64);
            if (change == "path") row.BlobKey = "outside/file";
            if (change == "request") row.UploadRequestId = Guid.NewGuid();
            if (change == "expiry") row.ExpiresAtUtc += 1;
            if (change == "entry") { row.State = "Bound"; row.EntryId = foreignEntry; row.ExpiresAtUtc = null; row.BoundAtUtc = f.Clock.GetUtcNow().ToUnixTimeMilliseconds(); }
            if (change == "shadow") db.Attachments.Add(new() { AttachmentId = Guid.NewGuid().ToString("D"), SessionId = s.Run.SessionId.ToString("D"), BlobKey = "legacy/shadow", DisplayName = "shadow", ContentType = "text/plain", Sha256Hex = new string('a', 64) });
            var error = await Record.ExceptionAsync(() => db.SaveChangesAsync()); Assert.True(error is DbUpdateException, change + ": " + error);
            Assert.Equal(19, Assert.IsType<SqliteException>(((DbUpdateException)error!).InnerException).SqliteErrorCode);
        }
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray(); var own = Array.FindIndex(migrations, m => m.EndsWith("TenantAttachmentOwnership", StringComparison.Ordinal)); Assert.True(own > 0);
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(migrations[own - 1])); Assert.False(db.Database.HasPendingModelChanges());
        }
        var reopened = new SqliteAttachmentStore(f.Contexts!, f.Clock, Path.Combine(f.Root, "attachments"));
        Assert.Equal(original, await reopened.FindTenantAsync(s.Tenant, s.Run.SessionId, id)); Assert.NotEmpty(await reopened.ReadTenantContentAsync(s.Tenant, s.Run.SessionId, id));
    }

    private sealed class AttachmentCommitFault : SaveChangesInterceptor
    {
        private bool fired;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!fired && data.Context!.ChangeTracker.Entries<AttachmentRecordRow>().Any(e => e.State == EntityState.Added)) { fired = true; throw new InvalidOperationException("controlled attachment metadata fault"); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class AttachmentFaultContexts(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
    [Fact]
    public async Task Tenant_attachment_SQLite_metadata_fault_rolls_back_publication_audit_and_security_then_owned_cleanup_removes_private_orphan()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, attachmentAuthority: true); var s = await Setup(f, "general-assistant", 32);
        string connection; await using (var db = f.Contexts!.CreateDbContext()) connection = db.Database.GetDbConnection().ConnectionString;
        var contexts = new AttachmentFaultContexts(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite(connection).AddInterceptors(new AttachmentCommitFault()).Options);
        var faulty = new SqliteIamStore(contexts, builtIns: f.BuiltIns, clock: f.Clock, attachmentBacking: (SqliteAttachmentStore)f.Attachments);
        var service = AttachmentService(f, mutations: faulty); var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Run.SessionId))!;
        var revision = (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision; var request = f.Ids.NewId();
        Task<Guid> Upload() => service.UploadAsync(s.Tenant, s.Run.SessionId, s.Owner, request, revision, session.Snapshot.Revision, "source.txt", "text/plain", new MemoryStream("Private staged bytes"u8.ToArray())).AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upload());
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision); Assert.DoesNotContain(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == "attachments.create");
        Assert.Empty((await ((ITenantAttachmentStore)f.Attachments).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 10)).Items);
        var physical = Path.Combine(f.Root, "attachments", "tenants", s.Tenant.ToString("N"), s.Run.SessionId.ToString("N")); Assert.Single(Directory.GetFiles(physical));
        var id = await Upload(); Assert.Equal(id, Assert.Single((await ((ITenantAttachmentStore)f.Attachments).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 10)).Items).AttachmentId);
        Assert.Equal(2, Directory.GetFiles(physical).Length);
        await f.RunService().CancelAsync(s.Tenant, s.Run.AgentRunId, s.Owner, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!.Revision);
        session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Run.SessionId))!;
        await f.Sessions.ChangeAsync(s.Tenant, s.Owner, new(s.Run.SessionId, session.Snapshot.Revision, TenantSessionAction.Delete), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.True(await new TenantAttachmentCleanupService((ITenantAttachmentCleanupStore)f.Attachments).RecoverAsync() >= 1); Assert.False(Directory.Exists(physical));
    }

    [Fact]
    public async Task Tenant_attachment_native_reads_verify_digest_and_refuse_links_while_cleanup_never_follows_an_outside_target()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, attachmentAuthority: true); var s = await Setup(f, "general-assistant", 32); var id = await UploadAttachment(f, s);
        var files = (ITenantAttachmentStore)f.Attachments; var row = (await files.FindTenantAsync(s.Tenant, s.Run.SessionId, id))!;
        var physical = Path.Combine(f.Root, "attachments", "tenants", s.Tenant.ToString("N"), s.Run.SessionId.ToString("N"), id.ToString("N"));
        await File.WriteAllTextAsync(physical, "Changed bytes"); await Assert.ThrowsAsync<AccessDeniedException>(() => AttachmentService(f).ReadContentAsync(s.Tenant, s.Run.SessionId, id, s.Owner).AsTask());
        File.Delete(physical); var outside = Path.Combine(f.Root, "outside.txt"); await File.WriteAllTextAsync(outside, "Outside bytes remain untouched"); File.CreateSymbolicLink(physical, outside);
        await Assert.ThrowsAsync<AccessDeniedException>(() => AttachmentService(f).ReadContentAsync(s.Tenant, s.Run.SessionId, id, s.Owner).AsTask());
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Run.SessionId))!;
        await AttachmentService(f).AbortAsync(s.Tenant, s.Run.SessionId, id, s.Owner, f.Ids.NewId(), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, session.Snapshot.Revision);
        Assert.True(await ((ITenantAttachmentCleanupStore)f.Attachments).CleanupAttachmentContentAsync(new(s.Tenant, s.Run.SessionId, id, row.Sha256Hex)));
        Assert.False(File.Exists(physical)); Assert.Equal("Outside bytes remain untouched", await File.ReadAllTextAsync(outside));
    }
    [Fact]
    public async Task Tenant_attachment_native_quota_counts_private_orphans_before_publication()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, attachmentAuthority: true); var s = await Setup(f, "general-assistant", 32); await UploadAttachment(f, s);
        var physical = Path.Combine(f.Root, "attachments", "tenants", s.Tenant.ToString("N"), s.Run.SessionId.ToString("N"));
        using (var sparse = new FileStream(Path.Combine(physical, f.Ids.NewId().ToString("N")), FileMode.CreateNew)) sparse.SetLength(AttachmentLimits.MaxBytesSession);
        var revision = (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        Assert.Equal("AttachmentQuotaExceeded", (await Assert.ThrowsAsync<AgentCoreException>(() => UploadAttachment(f, s))).Code);
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision); Assert.Single((await ((ITenantAttachmentStore)f.Attachments).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 10)).Items);
    }
}
