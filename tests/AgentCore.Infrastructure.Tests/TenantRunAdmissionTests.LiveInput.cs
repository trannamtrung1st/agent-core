using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private sealed class StoreLiveInput(Fixture f, TenantRuntimeFrame frame) : ITenantSessionInputControl
    {
        public TenantHumanInput? Prepared { get; private set; }
        public Func<Task>? Before { get; init; }
        public Func<TenantHumanInput, TenantHumanInput>? Change { get; init; }
        public TenantRuntimeFrame Frame { get; set; } = frame;
        private TenantRuntimeFrame OriginalFrame { get; } = frame;
        public async ValueTask<TenantHumanInputCommit> SubmitAsync(TenantHumanInput input, CancellationToken ct = default)
        {
            Prepared = input; if (Before is not null) await Before();
            if (Change is not null) input = Change(input);
            var snapshot = (await ((ITenantRuntimeStore)f.Iam).LoadTenantRuntimeAsync(OriginalFrame, ct))!;
            input = input with { Admission = input.Admission with { Input = input.Admission.Input with { Sequence = snapshot.DurableLastEntrySequence + 1 } } };
            return await ((ITenantRuntimeInputStore)f.Iam).SaveTenantRuntimeInputAsync(Frame,
                snapshot with { Revision = snapshot.Revision + 1, Summary = "Atomic live checkpoint" }, snapshot.Revision, input, f.Ids.NewId(), ct);
        }
    }
    private static TenantAttachmentService InputFiles(Fixture f) => new(f.Iam, (ITenantSessionStore)f.Iam, (ITenantAgentStore)f.Iam,
        (ITenantAttachmentStore)f.Attachments, (ITenantAttachmentMutationStore)f.Iam, f.Identity, f.Authorizer, f.Ids, f.Clock);

    [Theory]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "revoke-chat")] [InlineData(true, "revoke-chat")]
    [InlineData(false, "revoke-read")] [InlineData(true, "revoke-read")]
    [InlineData(false, "stale-security")] [InlineData(true, "stale-security")]
    [InlineData(false, "stale-instance")] [InlineData(true, "stale-instance")]
    [InlineData(false, "future-session")] [InlineData(true, "future-session")]
    [InlineData(false, "generation")] [InlineData(true, "generation")]
    [InlineData(false, "foreign-owner")] [InlineData(true, "foreign-owner")]
    [InlineData(false, "source-witness")] [InlineData(true, "source-witness")]
    public async Task Tenant_live_input_rechecks_prepared_human_frame_and_sources_without_partial_checkpoint_or_binding(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T12:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, attachmentAuthority: true); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        var grants = new Dictionary<string, Guid>();
        foreach (var permission in new[] { "agents.chat", "sessions.read", "attachments.read" })
        {
            var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, permission, [permission], await Revision());
            grants[permission] = (await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision())).AssignmentId;
        }
        var source = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Independent active task", await Revision(), 1);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, source.RunId))!;
        var claimed = await ClaimBackground(f, run);
        var file = await InputFiles(f).UploadAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), await Revision(), claimed.Snapshot.Revision, "queued.txt", "text/plain", new MemoryStream("future source"u8.ToArray()));
        var prior = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot;
        var adapter = new StoreLiveInput(f, claimed.Frame)
        {
            Before = async () => {
                if (boundary == "logout") await f.Identity.SignOutAsync(member.AuthenticationSessionId);
                if (boundary.StartsWith("revoke-", StringComparison.Ordinal)) await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, grants[boundary == "revoke-chat" ? "agents.chat" : "sessions.read"], await Revision());
            },
            Change = input => boundary switch {
                "stale-security" => input with { SecurityRevision = input.SecurityRevision - 1 },
                "stale-instance" => input with { Admission = input.Admission with { ExpectedInstanceRevision = input.Admission.ExpectedInstanceRevision - 1 } },
                "future-session" => input with { Admission = input.Admission with { ExpectedSessionRevision = prior.Revision + 20 } },
                "source-witness" => input with { Admission = input.Admission with { Attachments = input.Admission.Attachments!.Select(a => a with { ExpiresAt = clock.GetUtcNow().AddSeconds(-1) }).ToArray() } },
                _ => input }
        };
        if (boundary == "generation") adapter.Frame = claimed.Frame with { Generation = f.Ids.NewId() };
        if (boundary == "foreign-owner") adapter.Frame = claimed.Frame with { Owner = AgentRunOwner.ForTenant(f.Ids.NewId(), run.AgentInstanceId, run.Owner.ExecutorPrincipalId!.Value) };
        var securityRevision = await Revision();
        await Assert.ThrowsAnyAsync<Exception>(() => f.RunService(inputs: adapter).SubmitMessageAsync(setup.Tenant, setup.Session, member, f.Ids.NewId(), "Must not commit", [file], securityRevision, prior.Revision).AsTask());
        Assert.NotNull(adapter.Prepared);
        Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, adapter.Prepared.Admission.Run.AgentRunId));
        var after = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot;
        Assert.Equal(prior.Revision, after.Revision); Assert.Equal(prior.Summary, after.Summary);
        Assert.Equal(AttachmentState.Pending, (await ((ITenantAttachmentStore)f.Attachments).FindTenantAsync(setup.Tenant, setup.Session, file))!.State);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "agent-runs.admit" && a.Target == adapter.Prepared.Admission.Run.AgentRunId.ToString("D"));
        Assert.Equal(AgentRunStatus.Running, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!.Status);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_live_queue_is_durable_ordered_before_limit_and_preserves_each_human_root(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T13:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var source = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Active source", await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam; var runtime = (ITenantRuntimeStore)f.Iam;
        var active = await ClaimBackground(f, (await runs.FindTenantRunAsync(setup.Tenant, source.RunId))!);
        var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Live input requester", ["sessions.read", "agents.chat"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        Assert.False((await f.Authorizer.CheckAsync(member.PrincipalId, "agent-runs.create", AccessScope.ForTenant(setup.Tenant))).Allowed);
        var adapter = new StoreLiveInput(f, active.Frame); var service = f.RunService(inputs: adapter);
        var queued = new List<TenantRunReceipt>();
        for (var i = 0; i < 3; i++)
        {
            var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot;
            queued.Add(await service.SubmitTextAsync(setup.Tenant, setup.Session, i == 1 ? member : setup.Owner, f.Ids.NewId(), "Queued " + i, await Revision(), current.Revision));
        }
        var isolated = (await runtime.LoadTenantRuntimeAsync(active.Frame))!;
        Assert.Equal("Active source", Assert.Single(isolated.Entries).Text); Assert.Equal(4, isolated.DurableLastEntrySequence);
        Assert.Empty(await runtime.ListRunnableTenantRunsAsync(clock.GetUtcNow(), 1));
        clock.Advance(TimeSpan.FromSeconds(1));
        var unrelated = await f.Sessions.CreateAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner, SessionMode.Text, await Revision(), 1);
        var other = await f.RunService().SubmitTextAsync(setup.Tenant, unrelated.Owner.SessionId, setup.Owner, f.Ids.NewId(), "Other Session", await Revision(), 1);
        Assert.Equal(other.RunId, Assert.Single(await runtime.ListRunnableTenantRunsAsync(clock.GetUtcNow(), 1)).AgentRunId);
        await runtime.ApplyTenantRuntimeCommandAsync(active.Run.Owner, active.Run.AgentRunId, new AgentRunCommand.Complete(active.Run.Revision, clock.GetUtcNow(), active.Frame.Generation, "Done", AgentRunOutcomeKind.NoAction, null), f.Ids.NewId());
        Assert.Equal(queued[0].RunId, Assert.Single(await runtime.ListRunnableTenantRunsAsync(clock.GetUtcNow(), 1)).AgentRunId);
        var later = (await runs.FindTenantRunAsync(setup.Tenant, queued[1].RunId))!;
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => runtime.ApplyTenantRuntimeCommandAsync(later.Owner, later.AgentRunId, new AgentRunCommand.Claim(later.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId()).AsTask());
        if (sqlite) runtime = new SqliteIamStore(f.Contexts!, clock: clock);
        foreach (var receipt in queued)
        {
            var next = (await runs.FindTenantRunAsync(setup.Tenant, receipt.RunId))!;
            Assert.Equal(next.ActivationId, next.Admission.ExecutionContext!.RootActivationId);
            Assert.Equal(receipt == queued[1] ? member.PrincipalId : setup.Owner.PrincipalId, next.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
            var claimed = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Claim(next.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
            if (receipt == queued[0])
            {
                var retry = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Fail(claimed.Revision, clock.GetUtcNow(), claimed.Claim!.Generation, "provider-unavailable", "Retry source", true, clock.GetUtcNow().AddSeconds(1)), f.Ids.NewId());
                Assert.Equal(AgentRunStatus.WaitingToRetry, retry.Status);
                Assert.Equal(other.RunId, Assert.Single(await runtime.ListRunnableTenantRunsAsync(clock.GetUtcNow(), 1)).AgentRunId);
                clock.Advance(TimeSpan.FromSeconds(1));
                claimed = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Claim(retry.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
            }
            await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Complete(claimed.Revision, clock.GetUtcNow(), claimed.Claim!.Generation, "Done", AgentRunOutcomeKind.NoAction, null), f.Ids.NewId());
        }
        Assert.Equal(4, (await ((ITenantConversationStore)f.Iam).ReadTenantConversationAsync(setup.Tenant, setup.Session, 0, null, 100))!.History.Items.Count);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_queued_report_yields_to_later_human_input_without_circular_dispatch_wait(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T14:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var parent = await BackgroundParent(f); await WaitReadAuthority(f, parent); var child = await BackgroundChild(f, parent);
        var runtime = (ITenantRuntimeStore)f.Iam;
        await runtime.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var childLive = await ClaimBackground(f, child.Run); var outcome = BackgroundOutcome(f, childLive.Run, childLive.Snapshot);
        await runtime.CommitTenantOutcomeAsync(childLive.Frame, outcome.Snapshot, childLive.Snapshot.Revision, outcome.Complete, null, f.Ids.NewId());
        await CompleteBackgroundParent(f, parent);
        var completion = Assert.Single(await runtime.ListTenantBackgroundCompletionsAsync(100)); var report = await PrepareBackgroundReport(f, completion);
        await runtime.AdmitTenantBackgroundCompletionAsync(report, completion.Parent!.Snapshot.Revision, f.Ids.NewId());
        clock.Advance(TimeSpan.FromSeconds(1));
        var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(parent.Setup.Tenant, parent.Setup.Session))!.Snapshot;
        var human = await f.RunService().SubmitTextAsync(parent.Setup.Tenant, parent.Setup.Session, parent.Setup.Owner, f.Ids.NewId(), "New human request", (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision, current.Revision);
        Assert.Equal(human.RunId, Assert.Single(await runtime.ListRunnableTenantRunsAsync(clock.GetUtcNow(), 1)).AgentRunId);
        var next = await ClaimBackground(f, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(parent.Setup.Tenant, human.RunId))!);
        await runtime.ApplyTenantRuntimeCommandAsync(next.Run.Owner, next.Run.AgentRunId, new AgentRunCommand.Complete(next.Run.Revision, clock.GetUtcNow(), next.Frame.Generation, "Done", AgentRunOutcomeKind.NoAction, null), f.Ids.NewId());
        Assert.Equal(report.AgentRunId, Assert.Single(await runtime.ListRunnableTenantRunsAsync(clock.GetUtcNow(), 1)).AgentRunId);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_live_queue_capacity_refuses_atomically_and_exact_replay_still_succeeds(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var source = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Held active task", await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam; var sessions = (ITenantSessionStore)f.Iam;
        var active = await ClaimBackground(f, (await runs.FindTenantRunAsync(setup.Tenant, source.RunId))!);
        var adapter = new StoreLiveInput(f, active.Frame); var service = f.RunService(inputs: adapter); var message = f.Ids.NewId(); TenantRunReceipt? first = null;
        for (var i = 0; i < 99; i++)
        {
            var snapshot = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot;
            var receipt = await service.SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, i == 0 ? message : f.Ids.NewId(), "Bounded queued input", await Revision(), snapshot.Revision);
            first ??= receipt;
        }
        var before = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot; var security = await Revision();
        var replay = await service.SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, message, "Bounded queued input", security, before.Revision);
        Assert.False(replay.Created); Assert.Equal(first!.RunId, replay.RunId);
        var refusal = await Assert.ThrowsAsync<SecurityConflictException>(() => service.SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Overflow must not commit", security, before.Revision).AsTask());
        Assert.Equal("session_queue_full", refusal.ReasonCode);
        Assert.Equal(security, await Revision()); Assert.Equal(before.Revision, (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision);
        Assert.Equal(100, (await runs.ListTenantRunIdsAsync(setup.Tenant, setup.Session)).Count);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 1000), a => a.Action == "agent-runs.admit" && a.Target == adapter.Prepared!.Admission.Run.AgentRunId.ToString("D"));
    }

}
