using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Cancellation_preserves_durable_bytes_and_offsets_and_closes_an_attached_or_detached_claim_at_full_clock_precision(bool sqlite, bool attached)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T13:00:00.0000005Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f);
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Run", (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        var store = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var queued = (await runs.FindTenantRunAsync(setup.Tenant, receipt.RunId))!;
        var run = await store.ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
        var snapshot = (await store.LoadTenantRuntimeAsync(frame))!;
        var entry = new ConversationEntry(f.Ids.NewId(), snapshot.DurableLastEntrySequence + 1, null, ConversationRole.Assistant,
            "durable generated prefix", run.ResponseId, EntryStatus.Streaming, SessionMode.Text, 0, 0, clock.GetUtcNow());
        var saved = snapshot with { Revision = snapshot.Revision + 1, Entries = [.. snapshot.Entries, entry], LastEntrySequence = entry.Sequence,
            Status = attached ? SessionStatus.Attached : snapshot.Status, UpdatedAt = clock.GetUtcNow() };
        await store.SaveTenantRuntimeAsync(frame, saved, snapshot.Revision);
        var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var cancelled = await f.RunService().CancelAsync(setup.Tenant, run.AgentRunId, setup.Owner, revision, run.Revision);
        Assert.Equal(AgentRunStatus.Cancelled, cancelled.Status); Assert.Equal(run.Revision + 2, cancelled.Revision);
        Assert.Equal(revision + 1, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        ConversationEntry retained;
        if (sqlite)
        {
            await using var db = await f.Contexts!.CreateDbContextAsync();
            retained = SqliteMemoryStore.ToEntry(await db.Entries.SingleAsync(e => e.EntryId == entry.EntryId.ToString("D")));
        }
        else retained = ((InMemoryMemoryStore)f.Memory).FindEntry(run.SessionId, entry.EntryId)!;
        Assert.Equal(entry.Text, retained.Text); Assert.Equal(entry.ReceivedTextEndExclusive, retained.ReceivedTextEndExclusive);
        Assert.Equal(entry.HeardTextEndExclusive, retained.HeardTextEndExclusive); Assert.Equal(EntryStatus.Interrupted, retained.Status);
        Assert.Equal("userStop", retained.InterruptReason); Assert.NotNull(retained.CompletedAt);
        var metadata = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, run.SessionId))!.Snapshot;
        Assert.Equal(saved.Revision + 1, metadata.Revision); Assert.Equal(saved.Status, metadata.Status);
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.SaveTenantRuntimeAsync(frame, saved, snapshot.Revision).AsTask());
    }

    [Theory]
    [InlineData(false, "permission")] [InlineData(true, "permission")]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "owner")] [InlineData(true, "owner")]
    [InlineData(false, "generation")] [InlineData(true, "generation")]
    public async Task Prepared_cancellation_rechecks_human_authority_and_actual_owner_before_any_terminal_write(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Run", await Revision(), 1);
        var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Stop work", ["agent-runs.read", "agent-runs.cancel", "sessions.read", "agents.chat"], await Revision());
        var assignment = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, role.RoleId,
            AgentCore.Domain.Security.AccessScope.ForTenant(setup.Tenant), await Revision());
        var service = new TenantRunCancellationService(f.Iam, (ITenantRunStore)f.Iam, f.Identity, f.Authorizer, f.Ids, f.Clock);
        var request = await service.PrepareAsync(setup.Tenant, receipt.RunId, member, await Revision(), 1);
        if (boundary == "permission") await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, assignment.AssignmentId, await Revision());
        if (boundary == "logout") await f.Identity.SignOutAsync(member.AuthenticationSessionId);
        if (boundary == "owner") request = request with { Owner = AgentRunOwner.ForTenant(setup.Tenant, f.Ids.NewId(), f.Ids.NewId()) };
        if (boundary == "generation") request = request with { Generation = f.Ids.NewId() };
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.CommitAsync(request).AsTask());
        Assert.Equal(AgentRunStatus.Queued, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, receipt.RunId))!.Status);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 100), a => a.Action == "agent-runs.cancel");
    }
}
