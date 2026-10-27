using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Original_human_requires_current_Space_read_at_admission_and_effect_boundary_without_borrowing_executor_rights(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        await GrantWorkspaceRuntimeReads(f, s, ["spaces.read", "sessions.read", "workspaces.read", "workspaces.write"]);
        var requester = await User(f, s, "space-requester@example.test", ["agents.chat", "sessions.read", "workspaces.read", "workspaces.write"]);
        var session = await f.Sessions.CreateAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Owner, SessionMode.Text, await Revision(), 1);
        var beforeDenied = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.RunService().SubmitTextAsync(s.Tenant, session.Owner.SessionId, requester, f.Ids.NewId(), "Use this context", beforeDenied, 1).AsTask());
        Assert.Equal(1, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, session.Owner.SessionId))!.Snapshot.Revision);
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Read requested Space", ["spaces.read"], await Revision());
        var grant = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, requester.PrincipalId, role.RoleId, TenantSpaceService.Scope(s.Tenant, SpaceId(s)), await Revision());
        var admitted = await f.RunService().SubmitTextAsync(s.Tenant, session.Owner.SessionId, requester, f.Ids.NewId(), "Write the exact literal", await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam; var runtime = (ITenantRuntimeStore)f.Iam;
        var run = (await runs.FindTenantRunAsync(s.Tenant, admitted.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
        var call = new ModelToolCall("space-revoked-effect", ToolCatalog.WorkspaceWrite, """{"path":"/home/revoked.txt","content":"Exact literal"}""");
        await WorkspaceEffectCheckpoint(f, frame, call);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, await Revision());
        var writes = 0;
        var effects = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { writes++; return Task.CompletedTask; }));
        await Assert.ThrowsAsync<AccessDeniedException>(() => effects.ExecuteAsync(frame, call).AsTask());
        Assert.Equal(0, writes);
        Assert.Null(await ((ITenantDirectOperationStore)f.Iam).FindDirectOperationReceiptAsync(s.Tenant, run.AgentRunId, call.Id));
        Assert.Equal(AgentRunSideEffectDisposition.None, (await runs.FindTenantRunAsync(s.Tenant, run.AgentRunId))!.SideEffect.Disposition);
    }
}
