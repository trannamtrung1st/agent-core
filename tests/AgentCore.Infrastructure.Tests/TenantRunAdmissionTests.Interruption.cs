using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    public static TheoryData<bool, string> InterruptionBoundaries
    {
        get
        {
            var values = new TheoryData<bool, string>();
            foreach (var sqlite in new[] { false, true })
            foreach (var mode in new[] { "allowed", "allowed-fractional", "missing-cancel", "revoke-cancel", "revoke-run-read", "stale-run", "unknown-run", "wrong-session", "expired-claim", "generation", "changed-intent", "source-witness", "stale-security" }) values.Add(sqlite, mode);
            return values;
        }
    }
    [Theory] [MemberData(nameof(InterruptionBoundaries))]
    public async Task Tenant_interruption_rechecks_exact_target_human_and_file_before_one_atomic_admission(bool sqlite, string mode)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T16:00:00Z"));
        if (mode == "allowed-fractional") clock.Advance(TimeSpan.FromTicks(1234567));
        await using var f = await Fixture.Create(sqlite, clock, attachmentAuthority: true); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var runs = (ITenantRunStore)f.Iam; var sessions = (ITenantSessionStore)f.Iam; var runtime = (ITenantRuntimeStore)f.Iam;
        var first = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Original independent human objective", await Revision(), 1);
        var active = await ClaimBackground(f, (await runs.FindTenantRunAsync(setup.Tenant, first.RunId))!);
        var prefix = new ConversationEntry(f.Ids.NewId(), active.Snapshot.DurableLastEntrySequence + 1, null, ConversationRole.Assistant,
            "Captured old prefix", active.Run.ResponseId, EntryStatus.Streaming, SessionMode.Text, 0, 7, clock.GetUtcNow());
        await runtime.SaveTenantRuntimeAsync(active.Frame, active.Snapshot with { Revision = active.Snapshot.Revision + 1,
            Entries = [..active.Snapshot.Entries, prefix], LastEntrySequence = prefix.Sequence }, active.Snapshot.Revision);
        var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId); var grants = new Dictionary<string, Guid>();
        foreach (var permission in new[] { "agents.chat", "sessions.read", "attachments.read", "agent-runs.read", "agent-runs.cancel" })
        {
            if (mode == "missing-cancel" && permission == "agent-runs.cancel") continue;
            var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, permission, [permission], await Revision());
            grants[permission] = (await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision())).AssignmentId;
        }
        var file = await InputFiles(f).UploadAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), await Revision(),
            (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision, "new-root.txt", "text/plain", new MemoryStream("Exact fresh-root file"u8.ToArray()));
        var prior = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot;
        Guid? otherRun = null;
        if (mode == "wrong-session")
        {
            var other = await f.Sessions.CreateAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner, SessionMode.Text, await Revision(), 1);
            otherRun = (await f.RunService().SubmitTextAsync(setup.Tenant, other.Owner.SessionId, setup.Owner, f.Ids.NewId(), "Other owned Session", await Revision(), 1)).RunId;
        }
        var adapter = new StoreLiveInput(f, active.Frame) {
            Before = async () => {
                if (mode == "expired-claim") clock.Advance(TimeSpan.FromMinutes(6));
                if (mode is "revoke-cancel" or "revoke-run-read") await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant,
                    grants[mode == "revoke-cancel" ? "agent-runs.cancel" : "agent-runs.read"], await Revision());
            },
            Change = input => {
                if (mode is "revoke-cancel" or "revoke-run-read") input = input with { SecurityRevision = f.Iam.ReadTenantAsync(setup.Tenant).AsTask().GetAwaiter().GetResult()!.Tenant.Revision };
                return mode switch {
                    "changed-intent" => input with { Admission = input.Admission with { Interruption = input.Admission.Interruption! with { Revision = active.Run.Revision + 1 } } },
                    "source-witness" => input with { Admission = input.Admission with { Attachments = input.Admission.Attachments!.Select(a => a with { ExpiresAt = clock.GetUtcNow().AddSeconds(-1) }).ToArray() } },
                    "stale-security" => input with { SecurityRevision = input.SecurityRevision - 1 },
                    _ => input };
            }
        };
        if (mode == "generation") adapter.Frame = active.Frame with { Generation = f.Ids.NewId() };
        var target = new TenantInputInterruption(otherRun ?? (mode == "unknown-run" ? f.Ids.NewId() : active.Run.AgentRunId),
            active.Run.Revision - (mode == "stale-run" ? 1 : 0));
        var submitting = f.RunService(inputs: adapter).SubmitMessageAsync(setup.Tenant, setup.Session, member, f.Ids.NewId(),
            "Fresh human objective", [file], await Revision(), prior.Revision, interruption: target).AsTask();
        if (!mode.StartsWith("allowed", StringComparison.Ordinal))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => submitting);
            Assert.Equal(AgentRunStatus.Running, (await runs.FindTenantRunAsync(setup.Tenant, active.Run.AgentRunId))!.Status);
            var after = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot;
            Assert.Equal(prior.Revision, after.Revision); Assert.Equal(prior.Summary, after.Summary);
            Assert.Equal(AttachmentState.Pending, (await ((ITenantAttachmentStore)f.Attachments).FindTenantAsync(setup.Tenant, setup.Session, file))!.State);
            if (adapter.Prepared is { } prepared) Assert.Null(await runs.FindTenantRunAsync(setup.Tenant, prepared.Admission.Run.AgentRunId));
            Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "agent-runs.cancel" && a.Target == active.Run.AgentRunId.ToString("D"));
            return;
        }
        var receipt = await submitting; var saved = (await runs.FindTenantRunAsync(setup.Tenant, receipt.RunId))!;
        Assert.Equal(AgentRunStatus.Cancelled, (await runs.FindTenantRunAsync(setup.Tenant, active.Run.AgentRunId))!.Status);
        Assert.Equal(saved.ActivationId, saved.Admission.ExecutionContext!.RootActivationId); Assert.NotEqual(active.Run.ActivationId, saved.ActivationId);
        Assert.Equal(member.PrincipalId, saved.Admission.ExecutionContext.OriginalInitiatorPrincipalId); Assert.Null(saved.Admission.ExecutionContext.ParentRunId);
        Assert.Equal(receipt.RunId, Assert.Single(await runtime.ListRunnableTenantRunsAsync(clock.GetUtcNow(), 1)).AgentRunId);
        var bound = (await ((ITenantAttachmentStore)f.Attachments).FindTenantAsync(setup.Tenant, setup.Session, file))!;
        Assert.Equal(AttachmentState.Bound, bound.State); Assert.Equal(receipt.EntryId, bound.EntryId);
        ConversationEntry original;
        if (sqlite) { await using var db = f.Contexts!.CreateDbContext(); original = AgentCore.Infrastructure.Persistence.SqliteMemoryStore.ToEntry(await db.Entries.SingleAsync(e => e.EntryId == prefix.EntryId.ToString("D"))); }
        else original = ((AgentCore.Infrastructure.Persistence.InMemoryMemoryStore)f.Memory).FindEntry(setup.Session, prefix.EntryId)!;
        Assert.Equal(EntryStatus.Interrupted, original.Status); Assert.Equal(prefix.Text, original.Text); Assert.Equal(prefix.ReceivedTextEndExclusive, original.ReceivedTextEndExclusive); Assert.Equal("userSteer", original.InterruptReason);
        await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.LoadTenantRuntimeAsync(active.Frame).AsTask());
        var audit = await f.Iam.ReadAuditAsync(setup.Tenant, 200);
        Assert.Single(audit, a => a.Action == "agent-runs.cancel" && a.Target == active.Run.AgentRunId.ToString("D"));
        Assert.Single(audit, a => a.Action == "agent-runs.admit" && a.Target == receipt.RunId.ToString("D"));
    }
}

public sealed partial class TenantRunAdmissionTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_interruption_replaces_one_active_slot_at_capacity_without_reordering_accepted_inputs(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var source = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Active full-queue source", await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam; var sessions = (ITenantSessionStore)f.Iam; var runtime = (ITenantRuntimeStore)f.Iam;
        var active = await ClaimBackground(f, (await runs.FindTenantRunAsync(setup.Tenant, source.RunId))!);
        var adapter = new StoreLiveInput(f, active.Frame); var service = f.RunService(inputs: adapter); Guid? first = null;
        for (var i = 0; i < 99; i++)
        {
            var snapshot = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot;
            var queued = await service.SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Previously accepted input " + i, await Revision(), snapshot.Revision); first ??= queued.RunId;
        }
        var before = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot;
        var receipt = await service.SubmitMessageAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Exact full-queue replacement", [], await Revision(), before.Revision,
            interruption: new(active.Run.AgentRunId, active.Run.Revision));
        Assert.True(receipt.Created); Assert.Equal(AgentRunStatus.Cancelled, (await runs.FindTenantRunAsync(setup.Tenant, active.Run.AgentRunId))!.Status);
        Assert.Equal(101, (await runs.ListTenantRunIdsAsync(setup.Tenant, setup.Session)).Count);
        Assert.Equal(first, Assert.Single(await runtime.ListRunnableTenantRunsAsync(f.Clock.GetUtcNow(), 1)).AgentRunId);
        var saved = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot; var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        var proposed = adapter.Prepared!.Admission with { ExpectedSessionRevision = saved.Revision, Input = adapter.Prepared.Admission.Input with { Sequence = saved.DurableLastEntrySequence + 1 } };
        var replay = await runs.TryAdmitTenantHumanRunAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, proposed, setup.Owner,
            new(f.Ids.NewId(), setup.Tenant, setup.Owner.PrincipalId, "agent-runs.admit", proposed.Run.AgentRunId.ToString("D"), "allowed", "human_message_admitted", f.Clock.GetUtcNow()));
        Assert.NotNull(replay); Assert.False(replay.Created); Assert.Null(replay.Interrupted); Assert.Equal(receipt.RunId, replay.Run.AgentRunId); Assert.Equal(state.Tenant.Revision, await Revision());
    }
}
