using System.Text;
using AgentCore.Application.Execution;
using AgentCore.Application.Sessions;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Owned_Artifact_storage_and_human_reads_preserve_real_Run_and_independent_scopes(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, artifactAuthority: true); var setup = await Setup(f);
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(setup.Run.Owner, setup.Run.AgentRunId,
            new AgentRunCommand.Claim(setup.Run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
        var storage = (ITenantArtifactStore)f.Artifacts;
        var bytes = Encoding.UTF8.GetBytes("Owned private report"); var id = f.Ids.NewId();
        var record = await storage.CreateTenantAsync(frame, id, "report.txt", "text/plain", bytes);
        Assert.Equal(setup.Tenant, record.TenantId); Assert.Equal(run.AgentRunId, record.AgentRunId);
        Assert.Equal(record, await storage.CreateTenantAsync(frame, id, "report.txt", "text/plain", bytes));
        await Assert.ThrowsAsync<SecurityConflictException>(() => storage.CreateTenantAsync(frame, id, "report.txt", "text/plain", "other"u8.ToArray()).AsTask());
        bytes[0] = (byte)'X';
        var service = new TenantArtifactService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, storage, f.Identity, f.Authorizer);
        Assert.Equal(record, await service.ReadAsync(setup.Tenant, run.SessionId, id, setup.Owner));
        Assert.Equal("Owned private report", Encoding.UTF8.GetString((await service.ReadContentAsync(setup.Tenant, run.SessionId, id, setup.Owner)).Bytes));
        Assert.Equal(record, Assert.Single((await service.ListAsync(setup.Tenant, run.SessionId, setup.Owner)).Items));
        Assert.True(await f.Iam.OwnsResourceAsync(TenantArtifactRules.Scope(setup.Tenant, id)));
        Assert.False(await f.Iam.OwnsResourceAsync(TenantArtifactRules.Scope(Guid.NewGuid(), id)));
        Assert.Null(await storage.FindTenantAsync(Guid.NewGuid(), run.SessionId, id));
        Assert.False(f.Artifacts.Exists(run.SessionId, id)); Assert.Null(await f.Artifacts.GetAsync(run.SessionId, id)); Assert.Empty(await f.Artifacts.ListAsync(run.SessionId));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Artifacts.CreateAsync(run.SessionId, "bypass.txt", "text/plain", "bypass"u8.ToArray(), null, null).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Artifacts.DeleteSessionAsync(run.SessionId).AsTask());
        Assert.Equal(record, await service.ReadAsync(setup.Tenant, run.SessionId, id, setup.Owner));
        await Assert.ThrowsAsync<AccessDeniedException>(() => storage.CreateTenantAsync(frame with { Generation = Guid.NewGuid() }, f.Ids.NewId(), "forged.txt", "text/plain", "x"u8.ToArray()).AsTask());
        var reader = await User(f, setup, "artifact-reader@example.test", ["sessions.read"]);
        Assert.Empty((await service.ListAsync(setup.Tenant, run.SessionId, reader)).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, run.SessionId, id, reader).AsTask());
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Read this Artifact", ["artifacts.read"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var grant = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, reader.PrincipalId, role.RoleId, TenantArtifactRules.Scope(setup.Tenant, id), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Equal(record, Assert.Single((await service.ListAsync(setup.Tenant, run.SessionId, reader)).Items));
        Assert.Equal("Owned private report", Encoding.UTF8.GetString((await service.ReadContentAsync(setup.Tenant, run.SessionId, id, reader)).Bytes));
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Empty((await service.ListAsync(setup.Tenant, run.SessionId, reader)).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadContentAsync(setup.Tenant, run.SessionId, id, reader).AsTask());
        var artifactOnly = await User(f, setup, "artifact-only@example.test", ["artifacts.read"]);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadContentAsync(setup.Tenant, run.SessionId, id, artifactOnly).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => storage.CreateTenantAsync(frame, f.Ids.NewId(), "bad.txt", "not a MIME type", "x"u8.ToArray()).AsTask());
        await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.RequestCancellation(run.Revision, f.Clock.GetUtcNow(), null), f.Ids.NewId());
        await Assert.ThrowsAsync<AccessDeniedException>(() => storage.CreateTenantAsync(frame, f.Ids.NewId(), "late.txt", "text/plain", "x"u8.ToArray()).AsTask());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Owned_Artifact_pages_filter_current_scope_before_cursor_and_Run_visibility(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, artifactAuthority: true); var setup = await Setup(f);
        var frame = await ClaimArtifactRun(f, setup); var store = (ITenantArtifactStore)f.Artifacts;
        var ids = new[] { Guid.Parse("10000000-0000-0000-0000-000000000001"), Guid.Parse("20000000-0000-0000-0000-000000000002"), Guid.Parse("30000000-0000-0000-0000-000000000003") };
        foreach (var id in ids) await store.CreateTenantAsync(frame, id, "report.txt", "text/plain", "private"u8.ToArray());
        var reader = await User(f, setup, "paged-reader@example.test", ["sessions.read"]);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Selected Artifacts", ["artifacts.read"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        foreach (var id in ids.Where(id => id != ids[1])) await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, reader.PrincipalId,
            role.RoleId, TenantArtifactRules.Scope(setup.Tenant, id), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var service = ArtifactService(f, store); var first = await service.ListAsync(setup.Tenant, frame.SessionId, reader, limit: 1);
        Assert.True(first.HasMore); Assert.Equal(Assert.Single(first.Items).ArtifactId, first.NextCursor);
        var last = await service.ListAsync(setup.Tenant, frame.SessionId, reader, first.NextCursor, 1);
        Assert.False(last.HasMore); Assert.Null(last.NextCursor); Assert.Single(last.Items);
        Assert.Equal(ids.Where(id => id != ids[1]).Order(), first.Items.Concat(last.Items).Select(a => a.ArtifactId).Order());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(setup.Tenant, frame.SessionId, reader, ids[1], 1).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(setup.Tenant, frame.SessionId, reader, run: frame.RunId).AsTask());
        Assert.Equal(3, (await service.ListAsync(setup.Tenant, frame.SessionId, setup.Owner, run: frame.RunId)).Items.Count);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(setup.Tenant, frame.SessionId, setup.Owner, run: Guid.NewGuid()).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListAsync(setup.Tenant, frame.SessionId, setup.Owner, limit: 101).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, Guid.NewGuid(), ids[0], setup.Owner).AsTask());
    }

    [Theory] [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "delete")] [InlineData(true, "delete")]
    public async Task Owned_Artifact_download_withholds_bytes_after_original_authority_or_source_loss(bool sqlite, string change)
    {
        await using var f = await Fixture.Create(sqlite, artifactAuthority: true); var setup = await Setup(f);
        var frame = await ClaimArtifactRun(f, setup); var store = (ITenantArtifactStore)f.Artifacts; var id = f.Ids.NewId();
        await store.CreateTenantAsync(frame, id, "private.txt", "text/plain", "must be withheld"u8.ToArray());
        var reader = await User(f, setup, "download-reader@example.test", ["sessions.read", "artifacts.read"]);
        using var cancellation = new CancellationTokenSource();
        var hook = new ArtifactReadHook(store, async () =>
        {
            if (change == "logout") { await f.Identity.SignOutAsync(reader.AuthenticationSessionId); Assert.NotNull(await f.Identity.SignInAsync("download-reader@example.test", "strong-operation-password")); }
            else if (change == "cancel") cancellation.Cancel();
            else if (change == "revoke")
            {
                var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
                await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, state.Assignments.Single(a => a.PrincipalId == reader.PrincipalId).AssignmentId, state.Tenant.Revision);
            }
            else
            {
                var runtime = (ITenantRuntimeStore)f.Iam;
                var active = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
                var stopping = await runtime.ApplyTenantRuntimeCommandAsync(active.Owner, active.AgentRunId,
                    new AgentRunCommand.RequestCancellation(active.Revision, f.Clock.GetUtcNow(), null), f.Ids.NewId());
                await runtime.ApplyTenantRuntimeCommandAsync(active.Owner, active.AgentRunId,
                    new AgentRunCommand.CommitCancellation(stopping.Revision, f.Clock.GetUtcNow(), frame.Generation, null), f.Ids.NewId());
                var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!;
                await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(frame.SessionId, session.Snapshot.Revision, TenantSessionAction.Delete), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
            }
        });
        var attempt = ArtifactService(f, hook).ReadContentAsync(setup.Tenant, frame.SessionId, id, reader, cancellation.Token).AsTask();
        if (change == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        else await Assert.ThrowsAsync<AccessDeniedException>(() => attempt);
        if (change == "delete")
        {
            Assert.Null(await store.FindTenantAsync(setup.Tenant, frame.SessionId, id));
            Assert.False(await f.Iam.OwnsResourceAsync(TenantArtifactRules.Scope(setup.Tenant, id)));
            await Assert.ThrowsAsync<AccessDeniedException>(() => f.Artifacts.DeleteSessionAsync(frame.SessionId).AsTask());
        }
    }

    [Fact]
    public async Task Owned_Artifact_SQLite_reopens_exact_bytes_and_rejects_schema_drift_tampering_and_symlinks()
    {
        await using var f = await Fixture.Create(true, artifactAuthority: true); var setup = await Setup(f);
        var frame = await ClaimArtifactRun(f, setup); var id = f.Ids.NewId(); var bytes = "retained private bytes"u8.ToArray();
        var record = await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(frame, id, "private.txt", "text/plain", bytes);
        var reopened = new SqliteArtifactStore(f.Contexts!, f.Clock, Path.Combine(f.Root, "artifacts"));
        Assert.Equal(record, await reopened.FindTenantAsync(setup.Tenant, frame.SessionId, id));
        Assert.Equal(bytes, (await ArtifactService(f, reopened).ReadContentAsync(setup.Tenant, frame.SessionId, id, setup.Owner)).Bytes);
        await using (var db = f.Contexts!.CreateDbContext())
        {
            Assert.False(db.Database.HasPendingModelChanges());
            var keyId = id.ToString("D");
            foreach (var sql in new FormattableString[] { $"UPDATE Artifacts SET TenantId=NULL WHERE ArtifactId={keyId}",
                $"UPDATE Artifacts SET AgentRunId=NULL WHERE ArtifactId={keyId}", $"UPDATE Artifacts SET BlobKey='escape' WHERE ArtifactId={keyId}",
                $"UPDATE Artifacts SET DisplayName='changed' WHERE ArtifactId={keyId}" })
                await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql));
            var foreign = f.Ids.NewId().ToString("D"); var sid = frame.SessionId.ToString("D");
            var key = TenantArtifactRules.BlobKey(setup.Tenant, frame.SessionId, Guid.Parse(foreign));
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Artifacts (ArtifactId,TenantId,SessionId,AgentRunId,BlobKey,DisplayName,ContentType,ByteSize,Sha256Hex,CreatedAtUtc) VALUES ({foreign},{setup.Tenant},{sid},{foreign},{key},{"forged"},{"text/plain"},{record.ByteSize},{record.Sha256Hex},{0L})"));
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261018123638_TenantCredentialMetadataReview"));
        }
        var path = Path.Combine(f.Root, "artifacts", TenantArtifactRules.BlobKey(setup.Tenant, frame.SessionId, id));
        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)'x', bytes.Length).ToArray());
        await Assert.ThrowsAsync<AccessDeniedException>(() => ArtifactService(f, reopened).ReadContentAsync(setup.Tenant, frame.SessionId, id, setup.Owner).AsTask());
        File.Delete(path); var outside = Path.Combine(f.Root, "outside.txt"); await File.WriteAllBytesAsync(outside, bytes); File.CreateSymbolicLink(path, outside);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ArtifactService(f, reopened).ReadContentAsync(setup.Tenant, frame.SessionId, id, setup.Owner).AsTask());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(outside));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Owned_Artifact_publication_quotas_and_cancellation_leave_no_extra_record(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, artifactAuthority: true); var setup = await Setup(f); var frame = await ClaimArtifactRun(f, setup);
        ITenantArtifactStore store = sqlite ? new SqliteArtifactStore(f.Contexts!, f.Clock, Path.Combine(f.Root, "artifacts"), 3, 5)
            : new InMemoryArtifactStore(f.Clock, 3, 5).BindTenantAuthority((InMemoryMemoryStore)f.Memory);
        var id = f.Ids.NewId(); var record = await store.CreateTenantAsync(frame, id, "first.txt", "text/plain", "abc"u8.ToArray());
        Assert.Equal(record, await store.CreateTenantAsync(frame, id, "first.txt", "text/plain", "abc"u8.ToArray()));
        foreach (var bytes in new[] { "def"u8.ToArray(), "abcd"u8.ToArray() })
            Assert.Equal("ArtifactQuotaExceeded", (await Assert.ThrowsAsync<AgentCoreException>(() => store.CreateTenantAsync(frame, f.Ids.NewId(), "overflow.txt", "text/plain", bytes).AsTask())).Code);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateTenantAsync(frame, f.Ids.NewId(), "cancelled.txt", "text/plain", "d"u8.ToArray(), cancellation.Token).AsTask());
        Assert.Equal(record, Assert.Single((await store.ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items));
        Assert.Equal("abc"u8.ToArray(), await store.ReadTenantContentAsync(setup.Tenant, frame.SessionId, id));
        async Task<string> Attempt(Guid artifact)
        {
            try { await store.CreateTenantAsync(frame, artifact, "concurrent.txt", "text/plain", "de"u8.ToArray()); return "saved"; }
            catch (AgentCoreException error) { return error.Code; }
        }
        var outcomes = await Task.WhenAll(Attempt(f.Ids.NewId()), Attempt(f.Ids.NewId()));
        Assert.Single(outcomes, result => result == "saved"); Assert.Single(outcomes, result => result == "ArtifactQuotaExceeded");
        Assert.Equal(5, (await store.ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items.Sum(a => a.ByteSize));
    }

    [Fact]
    public async Task Owned_Artifact_migration_preserves_unowned_historical_bytes_without_adoption()
    {
        await using var f = await Fixture.Create(true, artifactAuthority: true);
        await using (var db = f.Contexts!.CreateDbContext())
            await db.GetService<IMigrator>().MigrateAsync("20261018123638_TenantCredentialMetadataReview");
        var sid = f.Ids.NewId(); var id = f.Ids.NewId(); var rid = f.Ids.NewId(); var bytes = "historical private report"u8.ToArray();
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var key = $"{sid:N}/{id:N}"; var path = Path.Combine(f.Root, "artifacts", key); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, bytes);
        await using (var db = f.Contexts!.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Artifacts (ArtifactId,SessionId,AgentRunId,BlobKey,DisplayName,ContentType,ByteSize,Sha256Hex,CreatedAtUtc) VALUES ({id.ToString("D")},{sid.ToString("D")},{rid.ToString("D")},{key},{"historical.txt"},{"text/plain"},{bytes.LongLength},{hash},{0L})");
            await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        }
        var metadata = (await f.Artifacts.GetAsync(sid, id))!; Assert.Null(metadata.TenantId); Assert.Equal(rid, metadata.AgentRunId);
        await using var stream = await f.Artifacts.OpenContentAsync(sid, id); using var output = new MemoryStream(); await stream.CopyToAsync(output); Assert.Equal(bytes, output.ToArray());
        Assert.Null(await ((ITenantArtifactStore)f.Artifacts).FindTenantAsync(f.Ids.NewId(), sid, id));
    }

    private static TenantArtifactService ArtifactService(Fixture f, ITenantArtifactStore store) => new(f.Iam, (ITenantSessionStore)f.Iam,
        (ITenantRunStore)f.Iam, store, f.Identity, f.Authorizer);
    private static async Task<TenantRuntimeFrame> ClaimArtifactRun(Fixture f, Environment setup)
    {
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(setup.Run.Owner, setup.Run.AgentRunId,
            new AgentRunCommand.Claim(setup.Run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        return new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
    }
    private sealed class ArtifactReadHook(ITenantArtifactStore inner, Func<Task> afterRead) : ITenantArtifactStore
    {
        public ValueTask<ArtifactRecord> CreateTenantAsync(TenantRuntimeFrame frame, Guid id, string name, string type, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => inner.CreateTenantAsync(frame, id, name, type, bytes, ct);
        public ValueTask<ArtifactRecord?> FindTenantAsync(Guid tenant, Guid session, Guid artifact, CancellationToken ct = default) => inner.FindTenantAsync(tenant, session, artifact, ct);
        public ValueTask<ArtifactPage> ListTenantAsync(Guid tenant, Guid session, IReadOnlyList<Guid>? allowed, Guid? before, int limit, Guid? run = null, CancellationToken ct = default) => inner.ListTenantAsync(tenant, session, allowed, before, limit, run, ct);
        public async ValueTask<byte[]> ReadTenantContentAsync(Guid tenant, Guid session, Guid artifact, CancellationToken ct = default)
        { var bytes = await inner.ReadTenantContentAsync(tenant, session, artifact, ct); await afterRead(); return bytes; }
    }
}
