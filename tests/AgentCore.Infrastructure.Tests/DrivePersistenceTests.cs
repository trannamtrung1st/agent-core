using AgentCore.Application.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Drive;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AgentCore.Infrastructure.Tests;

public sealed class DrivePersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connection_claim_remote_bytes_and_receipt_reopen_with_owner_isolation_and_CAS(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "drive-store-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={root}/drive.db").Options); IAgentInstanceStore owners; ICredentialStore credentials; IAgentCredentialBindingStore bindings; IDriveOperationStore operations; ISyntheticDriveStore files;
            if (sqlite) { await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync(); owners = new SqliteAgentInstanceStore(factory, new SystemIdGenerator(TimeProvider.System)); var c = new SqliteCredentialStore(factory); credentials = c; bindings = c; var d = new SqliteDriveStore(factory); operations = d; files = d; }
            else { var i = new InMemoryAgentInstanceStore(); owners = i; var c = new InMemoryCredentialStore(i); credentials = c; bindings = c; var d = new InMemoryDriveStore(); operations = d; files = d; }
            var owner = new AgentInstance(Guid.NewGuid(), "general-assistant", 27, new("Test", "Assistant", "Help", "Calm"), AgentInstanceLifecycle.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); await owners.InsertAsync(owner);
            var protector = new LocalCredentialProtector(root + "/keys"); var service = new CredentialService(credentials, bindings, protector, owners, new SystemIdGenerator(TimeProvider.System), TimeProvider.System); var account = await service.CreateGoogleDriveAccountAsync("Drive"); await service.BindAsync(owner.InstanceId, account.CredentialId, "drive", 1);
            var oauth = new GoogleDriveOAuthService(credentials, protector, new NoHttp(), new() { Enabled = true }, new(), TimeProvider.System, true); var start = await oauth.StartAsync(account.CredentialId, 1, owner.InstanceId, default); var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(start.AuthorizationUrl[start.AuthorizationUrl.IndexOf('?')..])["state"].ToString(); await oauth.CompleteAsync(state, start.Correlation, owner.InstanceId, "consent", null, default);
            var op = new DriveOperation(Guid.NewGuid(), owner.InstanceId, account.CredentialId, "effect-hash", 1, "drive.upload", "file-known", "{}", "Planned", 1, DateTimeOffset.UtcNow); await operations.ClaimAsync(op, default);
            await Assert.ThrowsAsync<AgentCoreException>(() => operations.ClaimAsync(op with { OperationId = Guid.NewGuid() }, default).AsTask()); await Assert.ThrowsAsync<AgentCoreException>(() => operations.ClaimAsync(op with { EffectHash = "different" }, default).AsTask());
            op = await operations.UpdateAsync(op with { State = "Dispatched", Revision = 2, ProtectedUploadSession = protector.Protect(account.CredentialId, 1, "opaque-session") }, 1, default); await Assert.ThrowsAsync<AgentCoreException>(() => operations.UpdateAsync(op with { Revision = 3 }, 1, default).AsTask());
            var file = new DriveFile("file-known", "report.txt", "text/plain", 2, "1", "49f68a5c8493ec2c0bf489821c21fc3b", ["root"], false); await files.SaveAsync(account.CredentialId, file, "hi"u8.ToArray(), null, default);
            if (sqlite) { SqliteConnection.ClearAllPools(); credentials = new SqliteCredentialStore(factory); operations = new SqliteDriveStore(factory); files = new SqliteDriveStore(factory); }
            var reopened = (await credentials.GetAsync(account.CredentialId))!; Assert.Equal(DriveConnectionStatus.Connected, reopened.DriveAccount!.ConnectionStatus); Assert.Equal("synthetic-drive-refresh", new LocalCredentialProtector(root + "/keys").Unprotect(account.CredentialId, 1, reopened.ProtectedPayload)); Assert.Null(await operations.GetAsync(Guid.NewGuid(), op.OperationId, default));
            var receipt = (await operations.GetAsync(owner.InstanceId, op.OperationId, default))!; Assert.Equal("Dispatched", receipt.State); Assert.Equal("hi"u8.ToArray(), await files.ReadAsync(account.CredentialId, file.Id, default)); Assert.Null(await files.GetAsync(Guid.NewGuid(), file.Id, default));
            Assert.Equal("opaque-session", new LocalCredentialProtector(root + "/keys").Unprotect(account.CredentialId, 1, receipt.ProtectedUploadSession!));
            await operations.UpdateAsync(receipt with { State = "Succeeded", ResultJson = System.Text.Json.JsonSerializer.Serialize(file), Revision = 3 }, 2, default); Assert.Null(await operations.FindAsync(owner.InstanceId, account.CredentialId, op.EffectHash, default));
            var failed = op with { OperationId = Guid.NewGuid(), EffectHash = "failed-effect", State = "Planned", Revision = 1, ProtectedUploadSession = null };
            await operations.ClaimAsync(failed, default);
            failed = await operations.UpdateAsync(failed with { State = "Dispatched", Revision = 2 }, 1, default);
            failed = await operations.UpdateAsync(failed with { State = "Indeterminate", Revision = 3 }, 2, default);
            await Assert.ThrowsAsync<AgentCoreException>(() => operations.UpdateAsync(failed with { State = "Failed", Revision = 4 }, 3, default).AsTask());
            await Assert.ThrowsAsync<AgentCoreException>(() => operations.UpdateAsync(failed with { State = "Failed", Revision = 4, ResultJson = "{}" }, 3, default).AsTask());
            failed = await operations.UpdateAsync(failed with { State = "Failed", Revision = 4,
                ResultJson = System.Text.Json.JsonSerializer.Serialize(new DriveOperationFailure("drive_scope_limited", "Check permission.", "initial_request_not_executed")) }, 3, default);
            if (sqlite) { SqliteConnection.ClearAllPools(); operations = new SqliteDriveStore(factory); }
            var failedReopened = (await operations.GetAsync(owner.InstanceId, failed.OperationId, default))!;
            Assert.Equal("Failed", failedReopened.State); Assert.Equal(failed.ResultJson, failedReopened.ResultJson);
            Assert.Equal(failed.CreatedAtUtc.ToUnixTimeMilliseconds(), failedReopened.CreatedAtUtc.ToUnixTimeMilliseconds());
            Assert.Null(await operations.FindAsync(owner.InstanceId, account.CredentialId, failed.EffectHash, default));
            await Assert.ThrowsAsync<AgentCoreException>(() => operations.UpdateAsync(failed with { State = "Dispatched", Revision = 5 }, 4, default).AsTask());
            Assert.Equal("Failed", (await operations.ClaimAsync(failed, default)).State);
            await operations.ClaimAsync(failed with { OperationId = Guid.NewGuid(), State = "Planned", Revision = 1, ResultJson = null }, default);
            var trashed = op with { OperationId = Guid.NewGuid(), EffectHash = "trashed-upload", State = "Planned", Revision = 1, ResultJson = null, ProtectedUploadSession = null };
            await operations.ClaimAsync(trashed, default);
            trashed = await operations.UpdateAsync(trashed with { State = "Dispatched", Revision = 2, ProtectedUploadSession = "protected-session" }, 1, default);
            var evidence = file with { Id = trashed.FileId, Trashed = true };
            string Proof(DriveFile observed) => System.Text.Json.JsonSerializer.Serialize(new DriveOperationFailure("drive_upload_target_trashed", "Known target is in Trash.", "verified_target_trashed", observed));
            await Assert.ThrowsAsync<AgentCoreException>(() => operations.UpdateAsync(trashed with { State = "Failed", Revision = 3, ResultJson = Proof(evidence) }, 2, default).AsTask());
            await Assert.ThrowsAsync<AgentCoreException>(() => operations.UpdateAsync(trashed with { State = "Failed", Revision = 3, ProtectedUploadSession = null, ResultJson = Proof(evidence with { Id = "other-file" }) }, 2, default).AsTask());
            await Assert.ThrowsAsync<AgentCoreException>(() => operations.UpdateAsync(trashed with { State = "Failed", Revision = 3, ProtectedUploadSession = null, ResultJson = Proof(evidence with { Trashed = false }) }, 2, default).AsTask());
            trashed = await operations.UpdateAsync(trashed with { State = "Failed", Revision = 3, ProtectedUploadSession = null, ResultJson = Proof(evidence) }, 2, default);
            if (sqlite) { SqliteConnection.ClearAllPools(); operations = new SqliteDriveStore(factory); }
            Assert.Equal(trashed.ResultJson, (await operations.GetAsync(owner.InstanceId, trashed.OperationId, default))!.ResultJson);
            Assert.Null(await operations.FindAsync(owner.InstanceId, account.CredentialId, trashed.EffectHash, default));
            await Assert.ThrowsAsync<AgentCoreException>(() => operations.UpdateAsync(trashed with { State = "Dispatched", Revision = 4 }, 3, default).AsTask());
            await operations.DeleteOwnerAsync(owner.InstanceId, default); Assert.Null(await operations.GetAsync(owner.InstanceId, op.OperationId, default)); Assert.NotNull(await files.GetAsync(account.CredentialId, file.Id, default));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Additive_migration_up_down_preserves_preexisting_General_and_Email_data()
    {
        var path = Path.Combine(Path.GetTempPath(), "drive-migration-" + Guid.NewGuid().ToString("N") + ".db"); var f = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await using var db = f.CreateDbContext(); var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync("20261013040224_EmailMonitoringEventSource"); var store = new SqliteCredentialStore(f); var now = DateTimeOffset.UtcNow; var general = new Credential(Guid.NewGuid(), "Sentinel", CredentialKind.Password, CredentialStatus.Active, new Dictionary<string, string>(), [], "protected-sentinel", 1, 1, now, now); await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Credentials (CredentialId, DisplayName, Kind, Status, MetadataJson, AllowedOriginsJson, ProtectedPayload, ProtectionVersion, Revision, CreatedAtUtc, UpdatedAtUtc, Purpose, SecretRevision) VALUES ({general.CredentialId.ToString("D")}, 'Sentinel', 'Password', 'Active', '{{}}', '[]', 'protected-sentinel', 1, 1, 0, 0, 'General', 0)");
            await migrator.MigrateAsync(); Assert.Equal(general.ProtectedPayload, (await store.GetAsync(general.CredentialId))!.ProtectedPayload); await migrator.MigrateAsync("20261013040224_EmailMonitoringEventSource"); Assert.Equal("protected-sentinel", await db.Credentials.AsNoTracking().Select(c => c.ProtectedPayload).SingleAsync()); Assert.DoesNotContain("20261013171144_GoogleDriveAccountsAndOperations", await db.Database.GetAppliedMigrationsAsync()); await migrator.MigrateAsync(); Assert.Equal(1, await db.Credentials.CountAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seven_file_batch_reopens_sqlite_receipts_and_does_not_repeat_remote_patches(bool deadline)
    {
        var root = Path.Combine(Path.GetTempPath(), "drive-batch-reopen-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var dbOptions = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={root}/store.db").Options;
            var factory = new Factory(dbOptions); await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync();
            var ids = new SystemIdGenerator(TimeProvider.System); var owners = new SqliteAgentInstanceStore(factory, ids); var credentials = new SqliteCredentialStore(factory); var files = new SqliteDriveStore(factory);
            var owner = new AgentInstance(Guid.NewGuid(), "general-assistant", 28, new("UAT", "Assistant", "Help", "Calm"), AgentInstanceLifecycle.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); await owners.InsertAsync(owner);
            var protector = new LocalCredentialProtector(root + "/keys"); var service = new CredentialService(credentials, credentials, protector, owners, ids, TimeProvider.System);
            var account = await service.CreateGoogleDriveAccountAsync("Batch Drive"); await service.BindAsync(owner.InstanceId, account.CredentialId, "drive", 1);
            var oauth = new GoogleDriveOAuthService(credentials, protector, new NoHttp(), new(), new(), TimeProvider.System, true); var start = await oauth.StartAsync(account.CredentialId, 1, owner.InstanceId, default);
            var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(start.AuthorizationUrl[start.AuthorizationUrl.IndexOf('?')..])["state"].ToString(); await oauth.CompleteAsync(state, start.Correlation, owner.InstanceId, "consent", null, default);
            var targets = new List<AgentCore.Application.Drive.DriveTrashTarget>();
            for (var i = 0; i < 8; i++) { var file = new DriveFile("exact-file-" + i, "sample-" + i + ".txt", "text/plain", 2, "1", "49f68a5c8493ec2c0bf489821c21fc3b", ["unavailable-parent"], false); await files.SaveAsync(account.CredentialId, file, "hi"u8.ToArray(), null, default); if (i < 7) targets.Add(new(file.Id, file.Version)); }
            var provider = new BatchCountingFactory(new GoogleDriveProviderFactory(new(), oauth, new NoHttp(), files, files, protector, ids, TimeProvider.System, true));
            var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
            var drive = new AgentCore.Application.Drive.DriveService(credentials, credentials, owners, provider, files, clock); var access = await drive.ResolveAsync(owner.InstanceId, "drive", null, true, default); var batch = Guid.NewGuid();
            AgentCore.Application.Drive.DriveTrashBatchResult result;
            if (deadline)
            {
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                provider.AfterTrash = async (_, ct) => { reached.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
                var pending = drive.TrashBatchAsync(access, batch, 1, targets, default).AsTask();
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); clock.Advance(TimeSpan.FromSeconds(60));
                result = await pending.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(1, result.UncertainCount); Assert.Equal(6, result.NotAttemptedCount); Assert.Equal(1, provider.Trashes);
            }
            else { result = await drive.TrashBatchAsync(access, batch, 1, targets, default); Assert.Equal(7, result.TrashedCount); Assert.Equal(7, provider.Trashes); }
            SqliteConnection.ClearAllPools(); factory = new Factory(dbOptions); files = new(factory); credentials = new(factory); owners = new(factory, ids);
            oauth = new(credentials, new LocalCredentialProtector(root + "/keys"), new NoHttp(), new(), new(), TimeProvider.System, true);
            provider = new(new GoogleDriveProviderFactory(new(), oauth, new NoHttp(), files, files, protector, ids, TimeProvider.System, true));
            drive = new(credentials, credentials, owners, provider, files, TimeProvider.System); access = await drive.ResolveAsync(owner.InstanceId, "drive", null, true, default);
            var replay = await drive.TrashBatchAsync(access, batch, 1, targets, default); Assert.Equal(7, replay.TrashedCount); Assert.Equal(deadline ? 6 : 0, provider.Trashes);
            if (!deadline) Assert.Equal(result.Items.Select(i => i.OperationId), replay.Items.Select(i => i.OperationId));
            else Assert.Equal(result.Items[0].OperationId, replay.Items[0].OperationId); Assert.False((await files.GetAsync(account.CredentialId, "exact-file-7", default))!.Trashed);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private sealed class BatchCountingFactory(IGoogleDriveProviderFactory inner) : IGoogleDriveProviderFactory
    {
        public Func<string, CancellationToken, Task>? AfterTrash;
        public int Trashes; public bool Enabled => inner.Enabled; public bool Configured => inner.Configured; public bool Synthetic => inner.Synthetic;
        public IGoogleDriveProvider Create(Guid id, long revision) => new BatchCountingProvider(inner.Create(id, revision), this);
    }
    private sealed class BatchCountingProvider(IGoogleDriveProvider inner, BatchCountingFactory count) : IGoogleDriveProvider
    {
        public ValueTask<string> GenerateIdAsync(CancellationToken ct) => inner.GenerateIdAsync(ct);
        public ValueTask<DriveFilePage> ListAsync(string? folder, string? query, string? cursor, int limit, CancellationToken ct) => inner.ListAsync(folder, query, cursor, limit, ct);
        public ValueTask<DriveFile> GetAsync(string id, CancellationToken ct) => inner.GetAsync(id, ct);
        public ValueTask CreateFolderAsync(string id, string name, string parent, CancellationToken ct) => inner.CreateFolderAsync(id, name, parent, ct);
        public ValueTask UploadAsync(string id, string name, string parent, string mime, ReadOnlyMemory<byte> bytes, DriveOperation op, CancellationToken ct) => inner.UploadAsync(id, name, parent, mime, bytes, op, ct);
        public ValueTask<byte[]> DownloadAsync(DriveFile file, CancellationToken ct) => inner.DownloadAsync(file, ct);
        public ValueTask RenameAsync(string id, string name, CancellationToken ct) => inner.RenameAsync(id, name, ct);
        public ValueTask MoveAsync(string id, string source, string destination, CancellationToken ct) => inner.MoveAsync(id, source, destination, ct);
        public async ValueTask TrashAsync(string id, CancellationToken ct) { count.Trashes++; await inner.TrashAsync(id, ct); if (count.AfterTrash is not null) await count.AfterTrash(id, ct); }
    }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext> { public AgentCoreDbContext CreateDbContext() => new(options); }
    private sealed class NoHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => throw new InvalidOperationException("Synthetic must not use HTTP"); }
}
