using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_runtime_fences_generation_scope_revision_and_later_input_in_both_stores(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var first = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "First request", await Revision(), 1);
        var second = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Later request", await Revision(), 2);
        var store = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var run = (await runs.FindTenantRunAsync(setup.Tenant, first.RunId))!;
        var generation = f.Ids.NewId();
        run = await store.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), generation, clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, generation);
        var snapshot = (await store.LoadTenantRuntimeAsync(frame))!;
        Assert.Equal("First request", Assert.Single(snapshot.Entries).Text);
        var history = (await store.ReadTenantRuntimeHistoryAsync(frame, 0, null, 100))!;
        Assert.Equal(first.EntryId, Assert.Single(history.Items).EntryId);
        var newest = (await store.ReadTenantRuntimeHistoryAsync(frame, null, null, 100))!;
        Assert.Equal(first.EntryId, Assert.Single(newest.Items).EntryId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.LoadTenantRuntimeAsync(frame with { Generation = f.Ids.NewId() }).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.LoadTenantRuntimeAsync(frame with { Owner = AgentRunOwner.ForTenant(f.Ids.NewId(), run.AgentInstanceId, run.Owner.ExecutorPrincipalId!.Value) }).AsTask());
        var other = (await runs.FindTenantRunAsync(setup.Tenant, second.RunId))!;
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => store.ApplyTenantRuntimeCommandAsync(other.Owner, other.AgentRunId,
            new AgentRunCommand.Claim(other.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId()).AsTask());
        var securityRevision = await Revision();
        var busy = await Assert.ThrowsAsync<SecurityConflictException>(() => f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Concurrent input", securityRevision, 3).AsTask());
        Assert.Equal("session_in_use", busy.ReasonCode);
        var changed = snapshot with { Revision = snapshot.Revision + 1, Summary = "Runtime checkpoint" };
        await store.SaveTenantRuntimeAsync(frame, changed, snapshot.Revision);
        await store.SaveTenantRuntimeAsync(frame, changed, snapshot.Revision); // Same-content retry.
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => store.SaveTenantRuntimeAsync(frame, changed with { Summary = "Stale overwrite" }, snapshot.Revision).AsTask());
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, clock: clock);
            Assert.Equal("Runtime checkpoint", (await reopened.LoadTenantRuntimeAsync(frame))!.Summary);
            Assert.Equal(first.EntryId, Assert.Single((await reopened.ReadTenantRuntimeHistoryAsync(frame, null, null, 100))!.Items).EntryId);
        }
        Assert.Empty(await f.Memory.ReadHistoryAsync(setup.Session, 0, 100));
        clock.Advance(TimeSpan.FromMinutes(6));
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.SaveTenantRuntimeAsync(frame, changed with { Revision = changed.Revision + 1 }, changed.Revision).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.ReadTenantRuntimeHistoryAsync(frame, 0, null, 100).AsTask());
        var recovered = await store.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Recover(run.Revision, clock.GetUtcNow()), f.Ids.NewId());
        var claimed = await store.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(recovered.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.LoadTenantRuntimeAsync(frame).AsTask());
        var staleRuntime = new TenantAgentRunStore(store, runs, f.Ids, frame);
        await Assert.ThrowsAsync<AccessDeniedException>(() => staleRuntime.ApplyAsync(claimed.Owner, claimed.AgentRunId,
            new AgentRunCommand.Renew(claimed.Revision, clock.GetUtcNow(), claimed.Claim!.Generation, clock.GetUtcNow().AddMinutes(5))).AsTask());
        Assert.Equal(changed.Revision, (await store.LoadTenantRuntimeAsync(frame with { Generation = claimed.Claim!.Generation }))!.Revision);
    }
}
