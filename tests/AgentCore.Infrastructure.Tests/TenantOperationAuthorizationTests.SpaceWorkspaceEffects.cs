using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    // Store-level integration harness. Live switches use the claimed runtime writer
    // facet; the HTTP/worker tests exercise the actual serialized mailbox.
    private sealed class WorkspaceContextControl(Fixture fixture) : ITenantSessionContextControl
    {
        internal TenantRuntimeFrame? Frame;
        public async ValueTask<TenantContextSwitchResult> SwitchContextAsync(TenantContextSwitch request, CancellationToken ct = default)
        {
            var store = (ITenantSessionContextStore)fixture.Iam;
            if (Frame is null) return await store.SwitchTenantQuiescentContextAsync(request, fixture.Ids.NewId(), ct);
            var saved = (await ((ITenantSessionStore)fixture.Iam).FindTenantSessionAsync(request.Owner.TenantId, request.Owner.SessionId, ct))!.Snapshot;
            var at = DateTimeOffset.FromUnixTimeMilliseconds(fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds());
            var selection = TenantSessionContextRules.Selection(request, saved.ContextSelection!, Frame.RunId, at);
            return await store.SaveTenantRuntimeContextAsync(Frame, saved with { ContextSelection = selection, Revision = saved.Revision + 1, UpdatedAt = at },
                saved.Revision, request, fixture.Ids.NewId(), ct);
        }
    }
    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)] [InlineData(false, true, true)] [InlineData(true, true, true)]
    public Task Captured_Shared_home_and_private_alias_execute_exact_direct_or_reviewed_effects_without_following_pending_selection(bool sqlite, bool privateAlias, bool reviewed) =>
        CapturedSpaceEffectWorkflow(sqlite, privateAlias, reviewed);

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public Task Space_membership_retains_a_terminal_Run_home_until_pending_or_approved_publication_expires(bool sqlite, bool approved) =>
        CapturedSpaceEffectWorkflow(sqlite, false, approved, publicationRetention: true);

    private async Task CapturedSpaceEffectWorkflow(bool sqlite, bool privateAlias, bool reviewed, bool publicationRetention = false)
    {
        var retentionClock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var f = await Fixture.Create(sqlite, retentionClock, builtInAuthority: true, artifactAuthority: publicationRetention); var s = await Setup(f, "general-assistant", 35);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        await GrantWorkspaceRuntimeReads(f, s, ["spaces.read", "sessions.read", "workspaces.read", "workspaces.write"]);
        var spaceStore = (ITenantSpaceStore)f.Iam; var sessions = (ITenantSessionStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var spaces = new TenantSpaceService(f.Iam, spaceStore, (ITenantAgentStore)f.Iam, f.Authorizer, f.RunConfiguration(),
            (ITenantCredentialBindingStore)f.Iam, (ITenantCredentialStore)f.Iam, f.Ids, f.Clock);
        var shared = await spaces.CreateAsync(s.Owner.PrincipalId, s.Tenant, f.Ids.NewId(), "Shared effect room", "", "", await Revision());
        await spaces.SetMemberAsync(s.Owner.PrincipalId, s.Tenant, shared.SpaceId, s.Agent.Instance.InstanceId, "", "", [], [], await Revision(), shared.Revision, 0);
        var createdSession = await f.Sessions.CreateAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Owner, SessionMode.Text, await Revision(), 1);
        var session = (await sessions.FindTenantSessionAsync(s.Tenant, createdSession.Owner.SessionId))!;
        var contextControl = new WorkspaceContextControl(f);
        var contexts = new TenantSessionContextService(f.Iam, sessions, runs, (ITenantAgentStore)f.Iam, spaceStore, (ITenantEnvironmentStore)f.Iam,
            f.Authorizer, f.Identity, contextControl);
        var selected = await contexts.SwitchAsync(s.Tenant, session.Owner.SessionId, s.Owner, f.Ids.NewId(), shared.SpaceId, null,
            await Revision(), session.Snapshot.Revision, session.Snapshot.ContextSelection!.Revision);
        var admitted = await f.RunService().SubmitTextAsync(s.Tenant, session.Owner.SessionId, s.Owner, f.Ids.NewId(), "Write the exact file", await Revision(), selected.SessionRevision);
        var run = (await runs.FindTenantRunAsync(s.Tenant, admitted.RunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
        var files = WorkspaceReader(f); var home = TenantWorkspaceRules.ForRun(run, "/home"); var own = TenantWorkspaceRules.ForRun(run, "/private");
        Assert.Equal(shared.SpaceId, home.OwnerId); Assert.Equal(SpaceId(s), own.OwnerId); Assert.NotEqual(home.OwnerId, own.OwnerId);
        await files.WriteTenantAsync(home, "/home/marker.txt", "Shared original"u8.ToArray(), _ => ValueTask.CompletedTask);
        await files.WriteTenantAsync(own, "/private/marker.txt", "Private original"u8.ToArray(), _ => ValueTask.CompletedTask);
        var path = privateAlias ? "/private/marker.txt" : "/home/marker.txt";
        if (publicationRetention)
        {
            var publicationAuthority = await GrantPublication(f, s);
            await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, publicationAuthority.RoleId, TenantSessionRules.Scope(s.Tenant, run.SessionId), await Revision());
            await Policies(f).SaveAsync(s.Tenant, s.Owner, "artifacts.create", new(OperationApprovalMode.Always, false), await Revision(), 0);
            var consent = ApprovalService(f, artifactSources: ArtifactSources(f));
            var retainedRun = (await runs.FindTenantRunAsync(s.Tenant, run.AgentRunId))!;
            var publication = (await consent.PrepareWorkspaceArtifactAsync(new(s.Tenant, run.AgentRunId),
                JsonSerializer.Serialize(new { path = "/home/marker.txt", displayName = "retained.txt" }), retainedRun.Revision, frame.Generation)).Request!;
            Assert.Equal(shared.SpaceId, publication.Operation.SpaceContext!.SpaceId);
            if (reviewed) await consent.DecideAsync(s.Tenant, publication.RequestId, s.Owner, true, publication.Operation.ActionHash, await Revision(), publication.Revision);
            retainedRun = (await runs.FindTenantRunAsync(s.Tenant, run.AgentRunId))!;
            retainedRun = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(retainedRun.Owner, retainedRun.AgentRunId,
                new AgentRunCommand.RequestCancellation(retainedRun.Revision, f.Clock.GetUtcNow(), null), f.Ids.NewId());
            Assert.Equal(AgentRunStatus.Cancelled, retainedRun.Status);
            var currentSpace = (await spaceStore.FindSpaceAsync(s.Tenant, shared.SpaceId))!;
            var currentMember = Assert.Single(await spaceStore.ReadMembershipsAsync(s.Tenant, shared.SpaceId));
            async Task RemoveMember() => await spaces.SetMemberAsync(s.Owner.PrincipalId, s.Tenant, shared.SpaceId, s.Agent.Instance.InstanceId,
                "", "", [], [], await Revision(), currentSpace.Revision, currentMember.Revision, remove: true);
            var beforeRemoval = await Revision();
            Assert.Equal("space_membership_has_active_work", (await Assert.ThrowsAsync<SecurityConflictException>(RemoveMember)).ReasonCode);
            Assert.Equal(beforeRemoval, await Revision());
            Assert.Equal("Shared original"u8.ToArray(), (await files.InspectTenantAsync(home, "/home/marker.txt", 100)).Bytes);
            Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, publication.Operation.OperationId));
            retentionClock.Advance(TimeSpan.FromMinutes(61));
            await RemoveMember();
            await spaces.DeleteAsync(s.Owner.PrincipalId, s.Tenant, shared.SpaceId, await Revision(), currentSpace.Revision + 1);
            Assert.True(await spaceStore.CanPurgeDeletedSpaceWorkspaceAsync(new(s.Tenant, shared.SpaceId)));
            Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, run.SessionId, null, null, 20)).Items);
            return;
        }
        var call = new ModelToolCall("captured-space-effect", ToolCatalog.WorkspaceWrite, JsonSerializer.Serialize(new { path, content = "Exact updated bytes" }));
        if (!reviewed)
        {
            contextControl.Frame = frame;
            var current = (await sessions.FindTenantSessionAsync(s.Tenant, run.SessionId))!.Snapshot;
            var pending = await contexts.SwitchAsync(s.Tenant, run.SessionId, s.Owner, f.Ids.NewId(), SpaceId(s), null,
                await Revision(), current.Revision, current.ContextSelection!.Revision);
            Assert.Equal("pending_next_turn", pending.State);
            await WorkspaceEffectCheckpoint(f, frame, call);
            Assert.Equal(TenantOperationOutcome.Completed, (await WorkspaceEffects(f).ExecuteAsync(frame, call)).Receipt!.Outcome);
        }
        else
        {
            await Policies(f).SaveAsync(s.Tenant, s.Owner, "workspaces.write", new(OperationApprovalMode.Always, false), await Revision(), 0);
            var approvals = ApprovalService(f, workspaceMutations: MutationTargets(f));
            run = (await runs.FindTenantRunAsync(s.Tenant, run.AgentRunId))!;
            var prepared = await approvals.PrepareWorkspaceMutationAsync(new(s.Tenant, run.AgentRunId), call, run.Revision, frame.Generation);
            var request = prepared.Request!; Assert.Equal("home:" + (privateAlias ? own.OwnerId : home.OwnerId).ToString("D"), request.ResourceId);
            Assert.Contains((await approvals.ListAsync(s.Tenant, s.Owner, new())).Items, item => item.Request.RequestId == request.RequestId);
            request = await approvals.DecideAsync(s.Tenant, request.RequestId, s.Owner, true, request.Operation.ActionHash, await Revision(), request.Revision);
            Assert.Equal(1, await approvals.DispatchPendingAsync());
            run = (await runs.FindTenantRunAsync(s.Tenant, run.AgentRunId))!;
            run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
                new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
            frame = new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
            Assert.Equal(TenantOperationOutcome.Completed, (await ExecuteApprovedWorkspace(WorkspaceEffects(f), frame, request)).Outcome);
        }
        Assert.Equal(privateAlias ? "Shared original"u8.ToArray() : "Exact updated bytes"u8.ToArray(), (await files.InspectTenantAsync(home, "/home/marker.txt", 100)).Bytes);
        Assert.Equal(privateAlias ? "Exact updated bytes"u8.ToArray() : "Private original"u8.ToArray(), (await files.InspectTenantAsync(own, "/private/marker.txt", 100)).Bytes);
        Assert.Equal(shared.SpaceId, (await runs.FindTenantRunAsync(s.Tenant, run.AgentRunId))!.Admission.Configuration!.SpaceContext!.SpaceId);
    }
}
