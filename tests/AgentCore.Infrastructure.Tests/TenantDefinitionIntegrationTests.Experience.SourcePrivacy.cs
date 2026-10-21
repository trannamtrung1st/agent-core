using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_private_Run_source_witness_remains_private_after_another_Run_derives_its_result(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceSettings(f, await MemoryOwner(f, operations: true), true);
        await MemoryPermissions(f, owner, "agents.read", "agents.chat", "sessions.read", "agent-runs.read", "memory.read", "memory.manage");
        var seed = await MemoryClaim(f, owner); await MemoryCommit(f, seed, await MemoryPlan(f, seed, Learned("PRIVATE_SOURCE_RESULT")));
        var original = await MemoryClaim(f, owner); var memory = await ((ITenantLearnedMemoryStore)f.Iam).ReadTenantRunMemoryAsync(original.Frame); Assert.NotEmpty(memory.Sources);
        var runtime = (ITenantRuntimeStore)f.Iam;
        await runtime.ApplyTenantRuntimeCommandAsync(original.Run.Owner, original.Run.AgentRunId, new AgentRunCommand.Checkpoint(original.Run.Revision, DateTimeOffset.UtcNow, original.Frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, "{}", ToolCallId: "private-observation", Name: ToolCatalog.AgentStateInspect)], executionState: new(TenantMemorySources: memory.Sources)), 1, 0, 180000, 0), null), f.Ids.NewId());
        var privateRun = await MemoryCommit(f, original, await MemoryPlan(f, original));
        var derived = await MemoryClaim(f, owner); var experiences = (ITenantExperienceStore)f.Iam;
        var source = await experiences.ObserveTenantExperienceSourceAsync(new(owner.Tenant, derived.Run.AgentRunId), derived.Frame.Generation, ExperienceSourceKind.AgentRun, privateRun.AgentRunId);
        Assert.Equal(owner.Human.PrincipalId, source.Witness.PrivateUserPrincipalId);
        var saved = await SaveExperienceSource(f, derived, source);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(saved.Checkpoint, out var messages));
        await runtime.ApplyTenantRuntimeCommandAsync(saved.Owner, saved.AgentRunId, new AgentRunCommand.Checkpoint(saved.Revision, DateTimeOffset.UtcNow, derived.Frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([..messages!, new(ModelRole.Tool, "{}", ToolCallId: "derived-work", Name: ToolCatalog.AgentStateInspect)], executionState: AgentRunToolCallCheckpoint.ReadExecutionState(saved.Checkpoint)), 2, 0, 180000, 0), null), f.Ids.NewId());
        var completed = await MemoryCommit(f, derived, await MemoryPlan(f, derived));
        Assert.Empty(TenantMemoryOperationReferences.Read(completed.Checkpoint)); Assert.Empty(TenantExperienceReferences.Facts(completed.Checkpoint));
        Assert.Single(TenantExperienceReferences.Sources(completed.Checkpoint));
        var account = await f.Identity.RegisterAsync("derived-source-peer@example.test", "Peer", "strong-derived-source-password");
        var role = await f.Access.SaveRoleAsync(owner.Human.PrincipalId, owner.Tenant, null, "Derived source independent reads", ["agents.read", "agents.chat", "sessions.create", "sessions.read", "agent-runs.create", "agent-runs.read", "memory.read", "memory.manage"], (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var invitation = await f.Access.InviteAsync(owner.Human.PrincipalId, owner.Tenant, account.LoginKey, role.RoleId, (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var member = await f.Access.AcceptInvitationAsync(account.AccountId, owner.Tenant, invitation.Invitation.InvitationId, invitation.Token);
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-derived-source-password"))!.Session;
        var peer = new HumanSessionReference(account.AccountId, login.SessionId, member.PrincipalId); var reviews = (ITenantExperienceReviewStore)f.Iam;
        await Assert.ThrowsAsync<AccessDeniedException>(() => reviews.ObserveOwnTenantExperienceReviewAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, ExperienceSourceKind.AgentRun, completed.AgentRunId).AsTask());
        Assert.Equal(owner.Human.PrincipalId, (await reviews.ObserveOwnTenantExperienceReviewAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, ExperienceSourceKind.AgentRun, completed.AgentRunId)).Witness.PrivateUserPrincipalId);
        var peerTurn = await MemoryClaim(f, owner, requester: peer);
        await Assert.ThrowsAsync<AccessDeniedException>(() => experiences.ObserveTenantExperienceSourceAsync(new(owner.Tenant, peerTurn.Run.AgentRunId), peerTurn.Frame.Generation, ExperienceSourceKind.AgentRun, completed.AgentRunId).AsTask());
    }
}
