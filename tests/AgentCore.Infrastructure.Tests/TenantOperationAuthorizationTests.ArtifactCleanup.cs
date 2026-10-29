using System.Collections.Concurrent;
using System.Reflection;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Deleted_Artifact_cleanup_requires_exact_committed_owner_and_preserves_metadata_and_neighbor_bytes(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, artifactAuthority: true); var setup = await Setup(f);
        var frame = await ClaimArtifactRun(f, setup); var store = (ITenantArtifactStore)f.Artifacts;
        var id = f.Ids.NewId(); var record = await store.CreateTenantAsync(frame, id, "private.txt", "text/plain", "private retained bytes"u8.ToArray());
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Exact retained Artifact", ["artifacts.read"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var grant = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Owner.PrincipalId, role.RoleId, TenantArtifactRules.Scope(setup.Tenant, id), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var cleanup = (ITenantArtifactCleanupStore)f.Artifacts;
        var owner = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!.Owner;
        Assert.Empty((await cleanup.ListDeletedArtifactOwnersAsync(null, 100)).Items);
        Assert.False(await cleanup.CleanupDeletedArtifactsAsync(new(owner, f.Clock.GetUtcNow())));
        Assert.True(ArtifactBytesExist(f, record));
        var neighbor = Path.Combine(f.Root, "artifacts", "tenants", f.Ids.NewId().ToString("N"), frame.SessionId.ToString("N"), id.ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(neighbor)!); await File.WriteAllTextAsync(neighbor, "other tenant");
        var unowned = Path.Combine(f.Root, "artifacts", frame.SessionId.ToString("N"), id.ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(unowned)!); await File.WriteAllTextAsync(unowned, "unowned namespace");
        var target = await DeleteArtifactSession(f, setup, frame);
        Assert.DoesNotContain((await f.Iam.ReadTenantAsync(setup.Tenant))!.Assignments, a => a.AssignmentId == grant.AssignmentId);
        await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Access after deletion", ["sessions.read"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Equal(new[] { target }, (await cleanup.ListDeletedArtifactOwnersAsync(null, 1)).Items);
        Assert.False(await cleanup.CleanupDeletedArtifactsAsync(target with { Owner = owner with { TenantId = f.Ids.NewId() } }));
        Assert.False(await cleanup.CleanupDeletedArtifactsAsync(target with { Owner = owner with { ExecutorPrincipalId = f.Ids.NewId() } }));
        Assert.False(await cleanup.CleanupDeletedArtifactsAsync(target with { DeletedAt = target.DeletedAt.AddMilliseconds(1) }));
        Assert.True(ArtifactBytesExist(f, record));
        Assert.Null(await store.FindTenantAsync(setup.Tenant, frame.SessionId, id));
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanup.CleanupDeletedArtifactsAsync(target, cancel.Token).AsTask());
        }
        Assert.True(ArtifactBytesExist(f, record));
        Assert.Equal(1, await new TenantArtifactCleanupService(cleanup).RecoverAsync());
        Assert.False(ArtifactBytesExist(f, record));
        Assert.True(await cleanup.CleanupDeletedArtifactsAsync(target));
        Assert.Equal("other tenant", await File.ReadAllTextAsync(neighbor)); Assert.Equal("unowned namespace", await File.ReadAllTextAsync(unowned));
        Assert.Equal(target, Assert.Single((await cleanup.ListDeletedArtifactOwnersAsync(null, 100)).Items));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Artifacts.DeleteSessionAsync(frame.SessionId).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.CreateTenantAsync(frame, id, "private.txt", "text/plain", "private retained bytes"u8.ToArray()).AsTask());
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext(); var retained = await db.Artifacts.SingleAsync(a => a.ArtifactId == id.ToString("D"));
            Assert.Equal(record.Sha256Hex, retained.Sha256Hex); Assert.Equal(setup.Tenant, retained.TenantId); Assert.Equal(frame.RunId.ToString("D"), retained.AgentRunId);
            Assert.False(db.Database.HasPendingModelChanges());
        }
        else
        {
            var metadata = (ConcurrentDictionary<Guid, ArtifactRecord>)typeof(InMemoryArtifactStore).GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Artifacts)!;
            Assert.Equal(record, metadata[id]);
        }
    }

    [Theory] [InlineData(false, TenantSessionAction.Archive)] [InlineData(true, TenantSessionAction.Archive)]
    [InlineData(false, TenantSessionAction.Complete)] [InlineData(true, TenantSessionAction.Complete)]
    public async Task Deleted_Artifact_cleanup_retains_archived_and_merely_terminal_owners(bool sqlite, TenantSessionAction action)
    {
        await using var f = await Fixture.Create(sqlite, artifactAuthority: true); var setup = await Setup(f); var frame = await ClaimArtifactRun(f, setup);
        var record = await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(frame, f.Ids.NewId(), "retain.txt", "text/plain", "retain"u8.ToArray());
        await CancelArtifactRun(f, setup, frame);
        var before = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!;
        await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(frame.SessionId, before.Snapshot.Revision, action), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var cleanup = (ITenantArtifactCleanupStore)f.Artifacts;
        Assert.Empty((await cleanup.ListDeletedArtifactOwnersAsync(null, 100)).Items);
        Assert.False(await cleanup.CleanupDeletedArtifactsAsync(new(before.Owner, f.Clock.GetUtcNow())));
        Assert.True(ArtifactBytesExist(f, record));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Deleted_Artifact_cleanup_rejects_direct_deletion_that_retains_exact_Artifact_grants(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, artifactAuthority: true); var setup = await Setup(f); var frame = await ClaimArtifactRun(f, setup);
        var record = await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(frame, f.Ids.NewId(), "retain.txt", "text/plain", "retain"u8.ToArray());
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Scoped read", ["artifacts.read"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Owner.PrincipalId, role.RoleId, TenantArtifactRules.Scope(setup.Tenant, record.ArtifactId), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        await CancelArtifactRun(f, setup, frame);
        var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!;
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        await Assert.ThrowsAsync<ArgumentException>(() => ((ITenantSessionStore)f.Iam).TryChangeTenantSessionAsync(next, state.Tenant.Revision,
            new(frame.SessionId, current.Snapshot.Revision, TenantSessionAction.Delete), setup.Owner, f.Clock.GetUtcNow(),
            new(f.Ids.NewId(), setup.Tenant, setup.Owner.PrincipalId, "sessions.delete", frame.SessionId.ToString("D"), "allowed", "test", f.Clock.GetUtcNow())).AsTask());
        Assert.Equal(current.Snapshot.Revision, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!.Snapshot.Revision);
        Assert.Equal(state.Tenant.Revision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.True(ArtifactBytesExist(f, record));
    }

    [Fact]
    public async Task Deleted_Artifact_cleanup_SQLite_reopens_tombstone_and_purges_staging_without_following_nested_links()
    {
        await using var f = await Fixture.Create(true, artifactAuthority: true); var setup = await Setup(f); var frame = await ClaimArtifactRun(f, setup);
        var record = await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(frame, f.Ids.NewId(), "purge.txt", "text/plain", "purge"u8.ToArray());
        var directory = Path.GetDirectoryName(Path.Combine(f.Root, "artifacts", TenantArtifactRules.BlobKey(setup.Tenant, frame.SessionId, record.ArtifactId)))!;
        await File.WriteAllTextAsync(Path.Combine(directory, f.Ids.NewId().ToString("N")), "uncommitted final bytes");
        await File.WriteAllTextAsync(Path.Combine(directory, record.ArtifactId.ToString("N") + ".partial-" + f.Ids.NewId().ToString("N")), "uncommitted partial bytes");
        var external = Path.Combine(f.Root, "retained-neighbor"); Directory.CreateDirectory(external); await File.WriteAllTextAsync(Path.Combine(external, "private.txt"), "preserve");
        Directory.CreateSymbolicLink(Path.Combine(directory, "redirect"), external);
        await DeleteArtifactSession(f, setup, frame);
        var reopened = new SqliteArtifactStore(f.Contexts!, f.Clock, Path.Combine(f.Root, "artifacts"));
        Assert.Equal(1, await new TenantArtifactCleanupService(reopened).RecoverAsync());
        Assert.False(Directory.Exists(directory)); Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(external, "private.txt")));
        Assert.Equal(1, await new TenantArtifactCleanupService(reopened).RecoverAsync()); Assert.False(Directory.Exists(directory));
    }

    [Theory] [InlineData("session")] [InlineData("tenant")] [InlineData("root")]
    public async Task Deleted_Artifact_cleanup_rejects_substituted_roots_and_converges_after_safe_recovery(string level)
    {
        await using var f = await Fixture.Create(true, artifactAuthority: true); var setup = await Setup(f); var frame = await ClaimArtifactRun(f, setup);
        var record = await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(frame, f.Ids.NewId(), "purge.txt", "text/plain", "purge"u8.ToArray());
        var target = await DeleteArtifactSession(f, setup, frame);
        var path = Path.Combine(f.Root, "artifacts"); if (level != "root") path = Path.Combine(path, "tenants", setup.Tenant.ToString("N"));
        if (level == "session") path = Path.Combine(path, frame.SessionId.ToString("N"));
        var retained = path + ".retained"; Directory.Move(path, retained);
        var external = Path.Combine(f.Root, "must-not-purge"); Directory.CreateDirectory(external); await File.WriteAllTextAsync(Path.Combine(external, "private.txt"), "preserve");
        Directory.CreateSymbolicLink(path, external);
        try
        {
            var failure = await Assert.ThrowsAsync<AgentCoreException>(() => ((ITenantArtifactCleanupStore)f.Artifacts).CleanupDeletedArtifactsAsync(target).AsTask());
            Assert.Equal("Forbidden", failure.Code); Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(external, "private.txt")));
        }
        finally { Directory.Delete(path); Directory.Move(retained, path); }
        Assert.True(await ((ITenantArtifactCleanupStore)f.Artifacts).CleanupDeletedArtifactsAsync(target)); Assert.False(ArtifactBytesExist(f, record));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Deleted_Artifact_cleanup_pages_actual_same_Account_tenants_and_retries_one_failure_without_starving_neighbors(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, artifactAuthority: true); var alpha = await Setup(f); var alphaFrame = await ClaimArtifactRun(f, alpha);
        var sibling = await NewArtifactCleanupSession(f, alpha.Tenant, alpha.Owner, alpha.Agent);
        var siblingFrame = await ClaimArtifactRun(f, sibling);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single();
        var betaState = await f.Access.CreateTenantAsync(system.PrincipalId, alpha.Owner.AccountId, "Beta", "beta");
        var betaOwner = alpha.Owner with { PrincipalId = betaState.Users.Single().PrincipalId };
        var betaAgent = await f.Agents.CreateAsync(betaOwner.PrincipalId, betaState.Tenant.TenantId, "examiner", 1, betaState.Tenant.Revision);
        var beta = await NewArtifactCleanupSession(f, betaState.Tenant.TenantId, betaOwner, betaAgent); var betaFrame = await ClaimArtifactRun(f, beta);
        var store = (ITenantArtifactStore)f.Artifacts;
        var records = new[] {
            await store.CreateTenantAsync(alphaFrame, f.Ids.NewId(), "alpha.txt", "text/plain", "alpha"u8.ToArray()),
            await store.CreateTenantAsync(betaFrame, f.Ids.NewId(), "beta.txt", "text/plain", "beta"u8.ToArray()),
            await store.CreateTenantAsync(siblingFrame, f.Ids.NewId(), "retain.txt", "text/plain", "retain"u8.ToArray()) };
        var siblingRole = await f.Access.SaveRoleAsync(alpha.Owner.PrincipalId, alpha.Tenant, null, "Retained sibling read", ["artifacts.read"], (await f.Iam.ReadTenantAsync(alpha.Tenant))!.Tenant.Revision);
        var siblingGrant = await f.Access.AssignRoleAsync(alpha.Owner.PrincipalId, alpha.Tenant, alpha.Owner.PrincipalId, siblingRole.RoleId, TenantArtifactRules.Scope(alpha.Tenant, records[2].ArtifactId), (await f.Iam.ReadTenantAsync(alpha.Tenant))!.Tenant.Revision);
        await DeleteArtifactSession(f, alpha, alphaFrame); await DeleteArtifactSession(f, beta, betaFrame);
        Assert.Contains((await f.Iam.ReadTenantAsync(alpha.Tenant))!.Assignments, a => a.AssignmentId == siblingGrant.AssignmentId);
        var cleanup = (ITenantArtifactCleanupStore)f.Artifacts;
        var first = await cleanup.ListDeletedArtifactOwnersAsync(null, 1); Assert.Single(first.Items); Assert.NotNull(first.NextCursor);
        var second = await cleanup.ListDeletedArtifactOwnersAsync(first.NextCursor, 1); Assert.Single(second.Items); Assert.Null(second.NextCursor);
        Assert.NotEqual(first.Items[0].Owner.SessionId, second.Items[0].Owner.SessionId);
        Assert.Equal(new[] { alpha.Tenant, beta.Tenant }.Order(), first.Items.Concat(second.Items).Select(t => t.Owner.TenantId).Order());
        Assert.Empty((await cleanup.ListDeletedArtifactOwnersAsync(second.Items[0].Owner.SessionId, 1)).Items);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cleanup.ListDeletedArtifactOwnersAsync(null, 101).AsTask());
        var hook = new ArtifactCleanupFailure(cleanup, first.Items[0].Owner.SessionId); var worker = new TenantArtifactCleanupService(hook);
        Assert.Equal(1, await worker.RecoverAsync());
        Assert.True(ArtifactBytesExist(f, records.Single(a => a.SessionId == first.Items[0].Owner.SessionId)));
        Assert.False(ArtifactBytesExist(f, records.Single(a => a.SessionId == second.Items[0].Owner.SessionId)));
        hook.Fail = false; Assert.Equal(2, await worker.RecoverAsync());
        Assert.False(ArtifactBytesExist(f, records[0])); Assert.False(ArtifactBytesExist(f, records[1]));
        Assert.True(ArtifactBytesExist(f, records[2])); Assert.Equal("retain"u8.ToArray(), await store.ReadTenantContentAsync(alpha.Tenant, siblingFrame.SessionId, records[2].ArtifactId));
    }

    private static async Task<Environment> NewArtifactCleanupSession(Fixture f, Guid tenant, TenantSessionInitiator actor, TenantAgent agent)
    {
        var space = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(tenant, agent.Instance.InstanceId))!;
        var role = await f.Access.SaveRoleAsync(actor.PrincipalId, tenant, null, "Read cleanup Agent context", ["spaces.read"], (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(actor.PrincipalId, tenant, agent.PrincipalId, role.RoleId, TenantSpaceService.Scope(tenant, space.SpaceId), (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        var session = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, actor, SessionMode.Text, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, agent.Instance.Revision);
        var accepted = await f.RunService().SubmitTextAsync(tenant, session.Owner.SessionId, actor, f.Ids.NewId(), "Publish a private file", (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
        return new(tenant, actor, agent, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, accepted.RunId))!);
    }

    private sealed class ArtifactCleanupFailure(ITenantArtifactCleanupStore inner, Guid failSession) : ITenantArtifactCleanupStore
    {
        public bool Fail { get; set; } = true;
        public ValueTask<TenantArtifactCleanupPage> ListDeletedArtifactOwnersAsync(Guid? after, int limit, CancellationToken ct = default) => inner.ListDeletedArtifactOwnersAsync(after, limit, ct);
        public ValueTask<bool> CleanupDeletedArtifactsAsync(TenantArtifactCleanupTarget target, CancellationToken ct = default) => Fail && target.Owner.SessionId == failSession
            ? ValueTask.FromException<bool>(new IOException("Private failure detail must not escape the worker.")) : inner.CleanupDeletedArtifactsAsync(target, ct);
    }

    private static bool ArtifactBytesExist(Fixture f, ArtifactRecord artifact) => f.Contexts is not null
        ? File.Exists(Path.Combine(f.Root, "artifacts", TenantArtifactRules.BlobKey(artifact.TenantId!.Value, artifact.SessionId, artifact.ArtifactId)))
        : ((ConcurrentDictionary<Guid, byte[]>)typeof(InMemoryArtifactStore).GetField("_blobs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Artifacts)!).ContainsKey(artifact.ArtifactId);

    private static async Task<TenantArtifactCleanupTarget> DeleteArtifactSession(Fixture f, Environment setup, TenantRuntimeFrame frame)
    {
        await CancelArtifactRun(f, setup, frame);
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, frame.SessionId))!;
        await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(frame.SessionId, session.Snapshot.Revision, TenantSessionAction.Delete), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        return Assert.Single((await ((ITenantArtifactCleanupStore)f.Artifacts).ListDeletedArtifactOwnersAsync(null, 100)).Items, t => t.Owner.SessionId == frame.SessionId);
    }
    private static async Task CancelArtifactRun(Fixture f, Environment setup, TenantRuntimeFrame frame)
    {
        var runtime = (ITenantRuntimeStore)f.Iam;
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
        var stopping = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.RequestCancellation(run.Revision, f.Clock.GetUtcNow(), null), f.Ids.NewId());
        await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.CommitCancellation(stopping.Revision, f.Clock.GetUtcNow(), frame.Generation, null), f.Ids.NewId());
    }
}
