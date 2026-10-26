using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static FileSessionWorkspace CleanupFilesystem(Fixture f) => new(Path.Combine(f.Root, "workspaces"), Path.Combine(f.Root, "templates"),
        sessions: f.Memory, instances: f.Instances, sandboxes: f.Sandboxes);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_working_cleanup_repeats_exact_commit_witness_and_recovers_failed_native_purge(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, artifactAuthority: true); var setup = await Setup(f); var frame = await ClaimArtifactRun(f, setup);
        var artifact = await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(frame, f.Ids.NewId(), "keep.txt", "text/plain", "artifact retained separately"u8.ToArray());
        var owner = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!.Owner;
        var native = CleanupFilesystem(f); var store = (ITenantWorkingCleanupStore)f.Iam;
        var path = AgentWorkspacePhysicalPaths.SessionRoot(Path.Combine(f.Root, "workspaces"), owner.AgentInstanceId, owner.SessionId); Directory.CreateDirectory(Path.Combine(path, "working")); await File.WriteAllTextAsync(Path.Combine(path, "working", "private.txt"), "private");
        Assert.Empty((await store.ListTenantWorkingCleanupAsync(null, 100)).Items);
        await CancelArtifactRun(f, setup, frame);
        var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!;
        await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(frame.SessionId, current.Snapshot.Revision, TenantSessionAction.Complete), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Null(await store.ReadTenantWorkingCleanupAsync(owner)); // Default retains terminal files.
        current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!;
        await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(frame.SessionId, current.Snapshot.Revision, TenantSessionAction.Delete), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var target = Assert.IsType<TenantWorkingCleanupTarget>(await store.ReadTenantWorkingCleanupAsync(owner));
        var checks = 0;
        Assert.False(await native.PurgeTenantWorkingAsync(target, _ => ValueTask.FromResult(++checks == 1))); Assert.Equal(2, checks); Assert.True(Directory.Exists(path));
        Assert.False(await native.PurgeTenantWorkingAsync(target with { SessionRevision = target.SessionRevision - 1 }, async ct => await store.ReadTenantWorkingCleanupAsync(owner, ct) == (target with { SessionRevision = target.SessionRevision - 1 })));
        using (var canceled = new CancellationTokenSource())
        { canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TenantWorkingCleanupService(store, native).CleanupAsync(owner, canceled.Token).AsTask()); }
        var failure = new WorkingCleanupFailure(native); var service = new TenantWorkingCleanupService(store, failure);
        Assert.Equal(0, await service.RecoverAsync()); Assert.True(Directory.Exists(path)); Assert.True(ArtifactBytesExist(f, artifact));
        failure.Fail = false;
        if (sqlite) store = new SqliteIamStore(f.Contexts!);
        Assert.Equal(1, await new TenantWorkingCleanupService(store, failure).RecoverAsync()); Assert.False(Directory.Exists(path)); Assert.True(ArtifactBytesExist(f, artifact));
        Assert.Equal(target, await store.ReadTenantWorkingCleanupAsync(owner)); // Metadata survives cleanup, so absence converges idempotently.
        Assert.True(await service.CleanupAsync(owner)); Assert.False(Directory.Exists(path));
    }

    [Theory] [InlineData("session")] [InlineData("sessions")] [InlineData("agent")] [InlineData("root")]
    public async Task Tenant_working_cleanup_refuses_substituted_native_roots_and_retries_after_safe_recovery(string level)
    {
        await using var f = await Fixture.Create(true); var setup = await Setup(f); var frame = await ClaimArtifactRun(f, setup);
        var owner = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!.Owner;
        await CancelArtifactRun(f, setup, frame); var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!;
        await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(frame.SessionId, current.Snapshot.Revision, TenantSessionAction.Delete), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var native = CleanupFilesystem(f);
        var root = Path.Combine(f.Root, "workspaces"); var path = AgentWorkspacePhysicalPaths.SessionRoot(root, owner.AgentInstanceId, owner.SessionId); Directory.CreateDirectory(Path.Combine(path, "working")); await File.WriteAllTextAsync(Path.Combine(path, "working", "private.txt"), "retained");
        var replaced = level switch { "session" => path, "sessions" => Path.GetDirectoryName(path)!, "agent" => AgentWorkspacePhysicalPaths.AgentRoot(root, owner.AgentInstanceId), _ => root };
        var retained = replaced + ".retained"; Directory.Move(replaced, retained);
        var outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside); await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "outside"); Directory.CreateSymbolicLink(replaced, outside);
        var service = new TenantWorkingCleanupService((ITenantWorkingCleanupStore)f.Iam, native);
        try
        {
            await Assert.ThrowsAsync<AgentCoreException>(() => service.CleanupAsync(owner).AsTask());
            Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "keep.txt"))); Assert.Equal(0, await service.RecoverAsync());
        }
        finally { Directory.Delete(replaced); Directory.Move(retained, replaced); }
        Assert.Equal(1, await service.RecoverAsync()); Assert.False(Directory.Exists(path)); Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "keep.txt")));
    }

    private sealed class WorkingCleanupFailure(ITenantWorkingCleanupBytes inner) : ITenantWorkingCleanupBytes
    {
        public bool Fail { get; set; } = true;
        public ValueTask<bool> PurgeTenantWorkingAsync(TenantWorkingCleanupTarget target, Func<CancellationToken, ValueTask<bool>> stillAllowed, CancellationToken ct = default) => Fail
            ? ValueTask.FromException<bool>(new IOException("Retained private native failure")) : inner.PurgeTenantWorkingAsync(target, stillAllowed, ct);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_working_cleanup_waits_confirmed_exact_lease_and_pins_its_revision_across_reopen(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var frame = await ClaimArtifactRun(f, setup);
        var owner = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!.Owner;
        await CancelArtifactRun(f, setup, frame); var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!;
        await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(frame.SessionId, current.Snapshot.Revision, TenantSessionAction.Delete), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var profile = new SandboxProfile("p-" + f.Ids.NewId().ToString("N"), 1, "Cleanup lease", "Retained evidence", true, "docker.io/library/python@sha256:" + new string('a', 64), TenantId: setup.Tenant);
        var lease = new SandboxLease(frame.SessionId, owner.AgentInstanceId, f.Ids.NewId(), 1, profile, SandboxLeaseState.Ready,
            "unconfirmed-native-resource", f.Clock.GetUtcNow(), f.Clock.GetUtcNow().AddHours(1), TenantId: setup.Tenant,
            ExecutorPrincipalId: owner.ExecutorPrincipalId, AdmissionRunId: frame.RunId, AdmissionToolCallId: "retained-working-lease");
        async Task SaveLease(SandboxLease value, bool create = false)
        {
            if (!sqlite)
            {
                var backing = (InMemorySandboxStore)f.Sandboxes;
                lock (backing.TenantGate) { if (create) Assert.True(backing.SaveTenantProfile(new(setup.Tenant, profile.Id, f.Clock.GetUtcNow()), profile, 0)); backing.SaveTenantLease(value); }
            }
            else
            {
                await using var db = f.Contexts!.CreateDbContext();
                if (create)
                {
                    db.Add(new TenantSandboxProfileRecord(profile.Id, setup.Tenant, f.Clock.GetUtcNow()));
                    db.Add(new SandboxProfileRow { Id = profile.Id, TenantId = setup.Tenant, Revision = 1, Json = JsonSerializer.Serialize(profile, SandboxRuntimePersistence.Json) }); db.Add(SandboxRuntimePersistence.Row(value));
                }
                else db.Entry(await db.Set<SandboxLeaseRow>().SingleAsync(l => l.SessionId == frame.SessionId.ToString("D"))).CurrentValues.SetValues(SandboxRuntimePersistence.Row(value));
                await db.SaveChangesAsync();
            }
        }
        await SaveLease(lease, true);
        async Task<AgentRunRecord> ReadRetainedRun()
        {
            if (!sqlite) return AgentRunStoreMapping.ToRecord(((InMemoryMemoryStore)f.Memory).ReadRetainedTenantOperationRun(setup.Tenant, frame.RunId)!);
            await using var db = f.Contexts!.CreateDbContext();
            return await db.AgentRuns.AsNoTracking().SingleAsync(r => r.AgentRunId == frame.RunId.ToString("D"));
        }
        var oldRun = await ReadRetainedRun();
        const string privateTranscript = "PRIVATE-OLD-RUN-WITH-UNCONFIRMED-LEASE";
        var document = JsonNode.Parse(oldRun.PayloadJson)!;
        document["checkpoint"] = JsonSerializer.SerializeToNode(new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([
            new(ModelRole.User, privateTranscript)]), 1, 12, 1000), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        oldRun.PayloadJson = document.ToJsonString();
        await SaveEffectTestRun(f, AgentRunStoreMapping.ToDomain(oldRun));
        Assert.Contains(privateTranscript, (await ReadRetainedRun()).PayloadJson);
        var store = sqlite ? (ITenantWorkingCleanupStore)new SqliteIamStore(f.Contexts!) : (ITenantWorkingCleanupStore)f.Iam;
        var native = CleanupFilesystem(f);
        var path = AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), owner.AgentInstanceId, owner.SessionId); Directory.CreateDirectory(path); await File.WriteAllTextAsync(Path.Combine(path, "keep.txt"), "keep until confirmed");
        var service = new TenantWorkingCleanupService(store, native);
        foreach (var state in new[] { SandboxLeaseState.Ready, SandboxLeaseState.Executing, SandboxLeaseState.Terminating, SandboxLeaseState.NeedsReconciliation, SandboxLeaseState.Failed })
        {
            lease = lease with { State = state, Revision = lease.Revision + 1 }; await SaveLease(lease);
            Assert.Null(await store.ReadTenantWorkingCleanupAsync(owner)); Assert.False(await service.CleanupAsync(owner)); Assert.True(Directory.Exists(path));
            Assert.DoesNotContain(privateTranscript, (await ReadRetainedRun()).PayloadJson);
        }
        lease = lease with { State = SandboxLeaseState.Terminated, Revision = lease.Revision + 1 }; await SaveLease(lease);
        var target = Assert.IsType<TenantWorkingCleanupTarget>(await store.ReadTenantWorkingCleanupAsync(owner));
        Assert.Equal(lease.Generation, target.LeaseGeneration); Assert.Equal(lease.Revision, target.LeaseRevision);
        lease = lease with { Revision = lease.Revision + 1 }; await SaveLease(lease);
        Assert.False(await native.PurgeTenantWorkingAsync(target, async ct => await store.ReadTenantWorkingCleanupAsync(owner, ct) == target)); Assert.True(Directory.Exists(path));
        Assert.Equal(1, await service.RecoverAsync()); Assert.False(Directory.Exists(path));
    }
}
