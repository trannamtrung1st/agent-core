using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Child_admission_rejects_a_stale_membership_capture_atomically_and_recovers_by_fresh_capture(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var parent = await BackgroundParent(f); var child = await BackgroundChild(f, parent); var s = parent.Setup;
        var pin = parent.Parent.Admission.Configuration!.SpaceContext!;
        var spaces = new TenantSpaceService(f.Iam, (ITenantSpaceStore)f.Iam, (ITenantAgentStore)f.Iam, f.Authorizer, f.RunConfiguration(),
            (ITenantCredentialBindingStore)f.Iam, f.Credentials, f.Ids, f.Clock);
        var space = (await ((ITenantSpaceStore)f.Iam).FindSpaceAsync(s.Tenant, pin.SpaceId))!;
        await spaces.SetMemberAsync(s.Owner.PrincipalId, s.Tenant, space.SpaceId, s.Agent.Instance.InstanceId, "Current responsibility", "Current situational instructions", [], [],
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, space.Revision, pin.MembershipRevision);
        var runtime = (ITenantRuntimeStore)f.Iam;
        var stale = await Assert.ThrowsAsync<SecurityConflictException>(() => AdmitTaskFixture(f, parent.Frame, child.Session, child.Run, f.Ids.NewId()).AsTask());
        Assert.Equal("space_context_changed", stale.ReasonCode);
        Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, child.Run.AgentRunId));
        Assert.Null(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, child.Session.SessionId));
        var refreshed = await BackgroundChild(f, parent);
        Assert.Equal("Current situational instructions", refreshed.Run.Admission.Configuration!.SpaceContext!.MembershipInstructions);
        Assert.True((await AdmitTaskFixture(f, parent.Frame, refreshed.Session, refreshed.Run, f.Ids.NewId())).Created);
        Assert.Equal(pin, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, parent.Parent.AgentRunId))!.Admission.Configuration!.SpaceContext);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Pending_Session_switch_does_not_retarget_an_already_claimed_parents_child_admission(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var parent = await BackgroundParent(f); var s = parent.Setup; var beforePin = parent.Parent.Admission.Configuration!.SpaceContext!;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var spaces = new TenantSpaceService(f.Iam, (ITenantSpaceStore)f.Iam, (ITenantAgentStore)f.Iam, f.Authorizer, f.RunConfiguration(),
            (ITenantCredentialBindingStore)f.Iam, f.Credentials, f.Ids, f.Clock);
        var destination = await spaces.CreateAsync(s.Owner.PrincipalId, s.Tenant, f.Ids.NewId(), "Next turn workspace", "", "Next turn instructions", await Revision());
        await spaces.SetMemberAsync(s.Owner.PrincipalId, s.Tenant, destination.SpaceId, s.Agent.Instance.InstanceId, "", "", [], [], await Revision(), destination.Revision, 0);
        var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Session))!;
        var request = new TenantContextSwitch(current.Owner, s.Owner, f.Ids.NewId(), destination.SpaceId, null, 0, await Revision(),
            current.Snapshot.Revision, current.Snapshot.ContextSelection!.Revision, s.Agent.Instance.Revision);
        var selection = TenantSessionContextRules.Selection(request, current.Snapshot.ContextSelection, parent.Parent.AgentRunId, f.Clock.GetUtcNow());
        var snapshot = current.Snapshot with { Revision = current.Snapshot.Revision + 1, ContextSelection = selection, UpdatedAt = f.Clock.GetUtcNow() };
        var changed = await ((ITenantSessionContextStore)f.Iam).SaveTenantRuntimeContextAsync(parent.Frame, snapshot, current.Snapshot.Revision, request, f.Ids.NewId());
        Assert.Equal("pending_next_turn", changed.State);
        var child = await BackgroundChild(f, parent with { Snapshot = snapshot });
        Assert.True(beforePin.SameLocation(child.Run.Admission.Configuration!.SpaceContext));
        Assert.NotEqual(destination.SpaceId, child.Session.ContextSelection!.SpaceId);
        Assert.Null(child.Session.ContextSelection.EffectiveAfterRunId); Assert.Null(child.Session.ContextSelection.TransitionId);
        Assert.True((await AdmitTaskFixture(f, parent.Frame, child.Session, child.Run, f.Ids.NewId())).Created);
        // An admitted Task replays its original child; a later desired selection cannot retarget it.
        var redirected = (await f.RunConfiguration().ResolveInSpaceAsync(s.Tenant, s.Agent.Instance.InstanceId, selection, default)).Configuration;
        var forged = child with { Session = child.Session with { ContextSelection = selection } };
        var replay = await AdmitTaskFixture(f, parent.Frame, forged.Session, forged.Run, f.Ids.NewId());
        Assert.False(replay.Created);
        Assert.True(beforePin.SameLocation(replay.Run.Admission.Configuration!.SpaceContext));
        var retainedChild = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, child.Session.SessionId))!;
        Assert.True(beforePin.MatchesSelection(retainedChild.Snapshot.ContextSelection));
        Assert.Equal(child.Run.AgentRunId, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, child.Run.AgentRunId))!.AgentRunId);
        Assert.Equal(selection, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Session))!.Snapshot.ContextSelection);
        Assert.Equal(beforePin, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, parent.Parent.AgentRunId))!.Admission.Configuration!.SpaceContext);
    }
}
