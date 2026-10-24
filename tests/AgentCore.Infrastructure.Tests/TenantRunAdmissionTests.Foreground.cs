using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private sealed class DirectForeground(Fixture f) : ITenantSessionForegroundControl
    {
        public ValueTask ContinueAsync(TenantSessionForeground request, CancellationToken ct = default) =>
            ((ITenantSessionForegroundStore)f.Iam).ContinueTenantQuiescentAsync(request, f.Ids.NewId(), ct);
    }
    private static TenantSessionForegroundService Foreground(Fixture f) => new(f.Iam, (ITenantSessionStore)f.Iam, (ITenantAgentStore)f.Iam, f.Identity, f.Authorizer, new DirectForeground(f));
    private static TenantActivityService Activity(Fixture f) => new(f.Iam, (ITenantSessionStore)f.Iam, (ITenantAgentStore)f.Iam, (ITenantRunStore)f.Iam, f.Identity, f.Authorizer);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_foreground_quiescent_keeps_initial_result_and_new_human_turn_uses_same_session_with_a_new_root(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T04:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var parent = await BackgroundParent(f); var child = await BackgroundChild(f, parent);
        var runtime = (ITenantRuntimeStore)f.Iam; var sessions = (ITenantSessionStore)f.Iam;
        await runtime.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        await Foreground(f).ContinueAsync(parent.Setup.Tenant, child.Session.SessionId, parent.Setup.Owner, (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision, child.Session.Revision);
        var claimed = await ClaimBackground(f, child.Run); var result = BackgroundOutcome(f, claimed.Run, claimed.Snapshot);
        await runtime.CommitTenantOutcomeAsync(claimed.Frame, result.Snapshot, claimed.Snapshot.Revision, result.Complete, null, f.Ids.NewId());
        await CompleteBackgroundParent(f, parent); clock.Advance(TimeSpan.FromSeconds(1));
        var current = (await sessions.FindTenantSessionAsync(parent.Setup.Tenant, child.Session.SessionId))!;
        var next = await f.RunService().SubmitTextAsync(parent.Setup.Tenant, child.Session.SessionId, parent.Setup.Owner, f.Ids.NewId(), "Continue this exact task", (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision, current.Snapshot.Revision);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(parent.Setup.Tenant, next.RunId))!;
        Assert.NotEqual(child.Run.Admission.ExecutionContext!.RootActivationId, run.Admission.ExecutionContext!.RootActivationId);
        Assert.Equal(child.Session.SessionId, run.SessionId); Assert.Equal(ExecutionOrigin.UserMessage, run.Admission.ExecutionContext.Origin);
        var detail = await Activity(f).ReadBackgroundAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, child.Session.SessionId, parent.Setup.Owner);
        Assert.Equal(child.Run.AgentRunId, detail.InitialRun!.RunId); Assert.Equal("Owned result", detail.InitialRun.OutcomeText);
        Assert.Equal(2, (await Activity(f).ListRunsAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, child.Session.SessionId, parent.Setup.Owner)).Total);
        Assert.Equal(run.AgentRunId, Assert.Single((await Activity(f).ListRunsAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, child.Session.SessionId, parent.Setup.Owner, pageSize: 1)).Items).RunId);
        Assert.Equal(2, (await f.Sessions.ListAsync(parent.Setup.Tenant, parent.Setup.Owner)).Total);
        Assert.Single(await f.Iam.ReadAuditAsync(parent.Setup.Tenant, 200), a => a.Action == "sessions.continue-in-chat");
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
            Assert.Equal(current.Snapshot.Origin, (await reopened.FindTenantSessionAsync(parent.Setup.Tenant, child.Session.SessionId))!.Snapshot.Origin);
        }
    }
    [Theory]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "stale")] [InlineData(true, "stale")]
    [InlineData(false, "archived")] [InlineData(true, "archived")]
    [InlineData(false, "generation")] [InlineData(true, "generation")]
    public async Task Tenant_foreground_prepared_write_rechecks_human_revision_lifecycle_and_live_generation_without_false_audit(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var parent = await BackgroundParent(f); var child = await BackgroundChild(f, parent);
        var runtime = (ITenantRuntimeStore)f.Iam; var sessions = (ITenantSessionStore)f.Iam;
        await runtime.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var request = await Foreground(f).PrepareAsync(parent.Setup.Tenant, child.Session.SessionId, parent.Setup.Owner, (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision, child.Session.Revision);
        if (boundary == "generation")
        {
            var claimed = await ClaimBackground(f, child.Run);
            var forged = claimed.Snapshot with { Surfaces = SessionSurface.ChatList | SessionSurface.BackgroundWork, Revision = claimed.Snapshot.Revision + 1 };
            await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.SaveTenantRuntimeAsync(claimed.Frame, forged, claimed.Snapshot.Revision).AsTask());
            await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantSessionForegroundStore)f.Iam).SaveTenantRuntimeForegroundAsync(claimed.Frame with { Generation = f.Ids.NewId() }, forged, claimed.Snapshot.Revision, request, f.Ids.NewId()).AsTask());
        }
        else
        {
            if (boundary == "logout") await f.Identity.SignOutAsync(parent.Setup.Owner.AuthenticationSessionId);
            if (boundary == "stale") request = request with { SecurityRevision = request.SecurityRevision - 1 };
            if (boundary == "archived")
            {
                await f.RunService().CancelAsync(parent.Setup.Tenant, child.Run.AgentRunId, parent.Setup.Owner, request.SecurityRevision, child.Run.Revision);
                var session = (await sessions.FindTenantSessionAsync(parent.Setup.Tenant, child.Session.SessionId))!;
                await f.Sessions.ChangeAsync(parent.Setup.Tenant, parent.Setup.Owner, new(child.Session.SessionId, session.Snapshot.Revision, TenantSessionAction.Archive), (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision);
                request = request with { SecurityRevision = (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision, ObservedSessionRevision = (await sessions.FindTenantSessionAsync(parent.Setup.Tenant, child.Session.SessionId))!.Snapshot.Revision };
            }
            await Assert.ThrowsAnyAsync<Exception>(() => ((ITenantSessionForegroundStore)f.Iam).ContinueTenantQuiescentAsync(request, f.Ids.NewId()).AsTask());
        }
        Assert.Equal(SessionSurface.BackgroundWork, (await sessions.FindTenantSessionAsync(parent.Setup.Tenant, child.Session.SessionId))!.Snapshot.Surfaces);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(parent.Setup.Tenant, 200), a => a.Action == "sessions.continue-in-chat");
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_activity_filters_scoped_reads_before_count_and_keeps_run_results_and_continue_authority_independent(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T06:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var parent = await BackgroundParent(f); var child = await BackgroundChild(f, parent);
        await ((ITenantRuntimeStore)f.Iam).AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var claimed = await ClaimBackground(f, child.Run); var outcome = BackgroundOutcome(f, claimed.Run, claimed.Snapshot);
        await ((ITenantRuntimeStore)f.Iam).CommitTenantOutcomeAsync(claimed.Frame, outcome.Snapshot, claimed.Snapshot.Revision, outcome.Complete, null, f.Ids.NewId());
        clock.Advance(TimeSpan.FromSeconds(1));
        var empty = await f.Sessions.CreateAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, parent.Setup.Owner, SessionMode.Text,
            (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision, parent.Setup.Agent.Instance.Revision);
        var all = await Activity(f).ListSessionsAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, parent.Setup.Owner, pageSize: 1);
        Assert.Equal(3, all.Total); Assert.Equal(empty.Owner.SessionId, Assert.Single(all.Items).Session.Owner.SessionId);
        Assert.Equal(0, (await Activity(f).ListRunsAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, empty.Owner.SessionId, parent.Setup.Owner)).Total);
        var member = await Member(f, parent.Setup.Tenant, parent.Setup.Owner.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision;
        async Task Grant(string name, string permission, AccessScope scope)
        {
            var role = await f.Access.SaveRoleAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, null, name, [permission], await Revision());
            await f.Access.AssignRoleAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, member.PrincipalId, role.RoleId, scope, await Revision());
        }
        await Grant("Activity agent read", "agents.read", AccessScope.ForResource(parent.Setup.Tenant, "agent", child.Run.AgentInstanceId.ToString("D")));
        await Grant("Exact child read", "sessions.read", TenantSessionRules.Scope(parent.Setup.Tenant, child.Session.SessionId));
        var activity = Activity(f);
        var page = await activity.ListSessionsAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, member, pageSize: 1);
        Assert.Equal(1, page.Total); Assert.Equal(child.Session.SessionId, Assert.Single(page.Items).Session.Owner.SessionId);
        Assert.Empty((await activity.ListSessionsAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, member, page: 1, pageSize: 1)).Items);
        var hidden = await activity.ReadBackgroundAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, child.Session.SessionId, member);
        Assert.Null(hidden.InitialRun); Assert.Null(hidden.CompletionDelivery); Assert.False(hidden.Session.CanContinue);
        Assert.Equal(0, (await activity.ListRunsAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, child.Session.SessionId, member)).Total);
        Assert.Equal(0, (await activity.ListAgentRunsAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, member)).Total);
        var backgroundPage = await activity.ListBackgroundAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, member, pageSize: 1);
        Assert.Equal(1, backgroundPage.Total); Assert.Null(Assert.Single(backgroundPage.Items).InitialRun);
        await Assert.ThrowsAsync<AccessDeniedException>(() => activity.ReadRunAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, child.Session.SessionId, child.Run.AgentRunId, member).AsTask());
        await Grant("Exact initial Run read", "agent-runs.read", TenantRunRules.Scope(parent.Setup.Tenant, child.Run.AgentRunId));
        var visible = await activity.ReadBackgroundAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, child.Session.SessionId, member);
        Assert.Equal("Owned result", visible.InitialRun!.OutcomeText); Assert.Null(visible.CompletionDelivery!.TargetSessionId); Assert.False(visible.Session.CanContinue);
        Assert.Single((await activity.ListRunsAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, child.Session.SessionId, member)).Items);
        var globalRuns = await activity.ListAgentRunsAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, member, pageSize: 1);
        Assert.Equal(1, globalRuns.Total); Assert.Equal(child.Run.AgentRunId, Assert.Single(globalRuns.Items).RunId);
        Assert.Empty((await activity.ListAgentRunsAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, member, page: 1, pageSize: 1)).Items);
        Assert.Equal("Owned result", (await activity.ReadAgentRunAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, child.Run.AgentRunId, member)).OutcomeText);
        await Assert.ThrowsAsync<AccessDeniedException>(() => activity.ReadAgentRunAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, parent.Parent.AgentRunId, member).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => Foreground(f).PrepareAsync(parent.Setup.Tenant, child.Session.SessionId, member, securityRevision: 0, sessionRevision: 1).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => activity.ReadSessionAsync(parent.Setup.Tenant, Guid.NewGuid(), child.Session.SessionId, member).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => activity.ReadSessionAsync(Guid.NewGuid(), child.Run.AgentInstanceId, child.Session.SessionId, member).AsTask());
        var beforeOpen = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(parent.Setup.Tenant, child.Session.SessionId))!;
        await Foreground(f).ContinueAsync(parent.Setup.Tenant, child.Session.SessionId, parent.Setup.Owner, await Revision(), beforeOpen.Snapshot.Revision);
        await Grant("Chat in an already visible Session", "agents.chat", AccessScope.ForResource(parent.Setup.Tenant, "agent", child.Run.AgentInstanceId.ToString("D")));
        var openable = await activity.ReadBackgroundAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, child.Session.SessionId, member);
        Assert.True(openable.Session.CanContinue); Assert.False(openable.Session.Session.CanManage);
        var latest = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(parent.Setup.Tenant, child.Session.SessionId))!;
        await f.Sessions.ChangeAsync(parent.Setup.Tenant, parent.Setup.Owner, new(child.Session.SessionId, latest.Snapshot.Revision, TenantSessionAction.Archive), await Revision());
        var archived = await activity.ReadBackgroundAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, child.Session.SessionId, member);
        Assert.NotNull(archived.Session.Session.ArchivedAt); Assert.Equal("Owned result", archived.InitialRun!.OutcomeText);
        Assert.Equal(1, (await activity.ListSessionsAsync(parent.Setup.Tenant, child.Run.AgentInstanceId, member, backgroundOnly: true)).Total);

    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_foreground_rechecks_independently_scoped_human_management_after_preparation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var parent = await BackgroundParent(f); var child = await BackgroundChild(f, parent);
        await ((ITenantRuntimeStore)f.Iam).AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var member = await Member(f, parent.Setup.Tenant, parent.Setup.Owner.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision;
        var role = await f.Access.SaveRoleAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, null, "Manage exactly this child", ["sessions.read", "sessions.manage"], await Revision());
        var grant = await f.Access.AssignRoleAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, member.PrincipalId, role.RoleId,
            TenantSessionRules.Scope(parent.Setup.Tenant, child.Session.SessionId), await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => Foreground(f).PrepareAsync(parent.Setup.Tenant, child.Session.SessionId, member, 0, child.Session.Revision).AsTask());
        role = await f.Access.SaveRoleAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, null, "Chat with exact Agent", ["agents.chat"], await Revision());
        await f.Access.AssignRoleAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, member.PrincipalId, role.RoleId,
            AccessScope.ForResource(parent.Setup.Tenant, "agent", child.Run.AgentInstanceId.ToString("D")), await Revision());
        var prepared = await Foreground(f).PrepareAsync(parent.Setup.Tenant, child.Session.SessionId, member, await Revision(), child.Session.Revision);
        await f.Access.RevokeAssignmentAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, grant.AssignmentId, await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantSessionForegroundStore)f.Iam).ContinueTenantQuiescentAsync(prepared, f.Ids.NewId()).AsTask());
        Assert.Equal(SessionSurface.BackgroundWork, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(parent.Setup.Tenant, child.Session.SessionId))!.Snapshot.Surfaces);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(parent.Setup.Tenant, 200), a => a.Action == "sessions.continue-in-chat");
    }

}
