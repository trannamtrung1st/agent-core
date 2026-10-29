using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory] [InlineData(false, false, false, false)] [InlineData(true, false, false, false)] [InlineData(false, true, false, false)] [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)] [InlineData(true, false, true, false)] [InlineData(false, true, true, false)] [InlineData(true, true, true, false)]
    [InlineData(false, false, true, true)] [InlineData(true, false, true, true)] [InlineData(false, true, true, true)] [InlineData(true, true, true, true)]
    public async Task Task_file_effect_transactions_repeat_worker_and_actual_requester_authority(bool sqlite, bool artifact, bool crossAgent, bool revokeWorker)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var setup = await Setup(f, "general-assistant", 35);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var target = crossAgent ? await f.AgentsAtRelease(35).CreateAsync(setup.Owner.PrincipalId, setup.Tenant, "general-assistant", 35, await Revision()) : setup.Agent;
        var requester = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        await GrantExactContextRead(f, setup, requester.PrincipalId);
        var chat = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Exact chat", ["agents.chat"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, requester.PrincipalId, chat.RoleId, AccessScope.ForResource(setup.Tenant, "agent", setup.Agent.Instance.InstanceId.ToString("D")), await Revision());
        var read = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Root conversation only", ["sessions.read"], await Revision());
        var parentRead = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, requester.PrincipalId, read.RoleId, TenantSessionRules.Scope(setup.Tenant, setup.Session), await Revision());
        var executor = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Independent background file authority",
            ["sessions.create", "agent-runs.create", "agents.read", "agents.chat", "sessions.read", "spaces.read", "tasks.read", "tasks.delegate", "workspaces.read", "workspaces.write", "artifacts.create"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Agent.PrincipalId, executor.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        RoleAssignment? workerEffects = null; AccessRole? workerEffectRole = null;
        if (crossAgent)
        {
            var baseline = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Worker independent admission and sources",
                executor.Permissions.Where(key => key is not ("workspaces.write" or "artifacts.create")).ToArray(), await Revision());
            await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, target.PrincipalId, baseline.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
            workerEffectRole = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Worker own native primary operations", ["workspaces.write", "artifacts.create"], await Revision());
            workerEffects = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, target.PrincipalId, workerEffectRole.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        }
        var intent = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Independent primary intent authority", ["sessions.create", "agent-runs.create", "tasks.delegate", "spaces.read", "workspaces.write", "artifacts.create"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, requester.PrincipalId, intent.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, requester, f.Ids.NewId(), "Owned file task", await Revision(), 1);
        var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var parent = await ClaimBackground(f, (await runs.FindTenantRunAsync(setup.Tenant, receipt.RunId))!);
        var seed = new BackgroundSetup(setup, parent.Run, parent.Snapshot, parent.Frame, null); var child = await BackgroundChild(f, seed, targetInstance: target.Instance.InstanceId);
        await AdmitTaskFixture(f, parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var active = await ClaimBackground(f, child.Run);
        Assert.Equal(target.PrincipalId, active.Run.Owner.ExecutorPrincipalId);
        Assert.Equal(target.Instance.InstanceId, active.Run.AgentInstanceId);
        Assert.Equal(ExecutionOrigin.DelegatedTask, active.Run.Admission.ExecutionContext!.Origin);
        Assert.Equal(setup.Agent.Instance.InstanceId, child.Admission.Task.Source.InstanceId);
        if (crossAgent)
        {
            Assert.NotEqual(parent.Run.Owner.ExecutorPrincipalId, active.Run.Owner.ExecutorPrincipalId);
            Assert.NotEqual(parent.Run.Admission.Configuration!.SpaceContext!.SpaceId, active.Run.Admission.Configuration!.SpaceContext!.SpaceId);
        }
        Assert.False((await f.Authorizer.CheckAsync(requester.PrincipalId, "sessions.read", TenantSessionRules.Scope(setup.Tenant, child.Run.SessionId))).Allowed);
        if (!artifact)
        {
            var call = new ModelToolCall("background-file-boundary", ToolCatalog.WorkspaceWrite, """{"path":"/home/transaction.txt","content":"Exact file"}""");
            var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 100000, 100);
            var changed = await runtime.ApplyTenantRuntimeCommandAsync(active.Run.Owner, active.Run.AgentRunId, new AgentRunCommand.Checkpoint(active.Run.Revision, f.Clock.GetUtcNow(), active.Frame.Generation, checkpoint, null), f.Ids.NewId());
            active = (changed, active.Frame, active.Snapshot);
        }
        var files = new FileSpaceWorkspaceStore(Path.Combine(f.Root, "workspaces"), (ITenantSpaceStore)f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantAgentDecommissionStore)f.Iam, f.Sandboxes);
        await files.WriteTenantAsync(TenantWorkspaceRules.ForRun(active.Run, "/home"), "/home/transaction-source.txt", "Exact file"u8.ToArray(), _ => ValueTask.CompletedTask);
        var captured = await files.CaptureArtifactSourceTenantAsync(TenantWorkspaceRules.ForRun(active.Run, "/home"), "/home/transaction-source.txt");
        async Task<TenantOperationReceipt?> Commit()
        {
            var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!; var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
            if (artifact)
            {
                var args = TenantWorkspaceArtifactContract.Parse("""{"path":"/home/transaction-source.txt","displayName":"transaction.txt"}""");
                var commit = new TenantArtifactCommit(f.Ids.NewId(), active.Run.AgentRunId, active.Frame.Generation, active.Run.Revision, "background-artifact-boundary", args.CanonicalArguments, args.ParametersHash,
                    active.Run.Admission.Configuration!.InstanceRevision, TenantArtifactToolContract.ActionHash(active.Run.SessionId, args.ParametersHash), TenantArtifactSourceRules.Witness(captured.Observation), captured);
                return await ((ITenantArtifactOperationStore)f.Iam).TryCommitArtifactAsync(next, state.Tenant.Revision, commit,
                    new(f.Ids.NewId(), setup.Tenant, active.Run.Owner.ExecutorPrincipalId!.Value, TenantArtifactOperationRules.AuditAction, active.Run.SessionId.ToString("D"), "allowed", "operation_completed", f.Clock.GetUtcNow()));
            }
            var parsed = TenantWorkspaceMutationContract.Parse(active.Run.SessionId, ToolCatalog.WorkspaceWrite, """{"path":"/home/transaction.txt","content":"Exact file"}""");
            var operation = new AgentRunDirectOperation(f.Ids.NewId(), "background-file-boundary", ToolCatalog.WorkspaceWrite, parsed.CanonicalArguments, parsed.ParametersHash, active.Run.Admission.Configuration!.InstanceRevision, setup.Agent.Instance.PersonaRevision);
            var start = await ((ITenantWorkspaceOperationStore)f.Iam).TryStartWorkspaceEffectAsync(next, state.Tenant.Revision,
                new(active.Run.AgentRunId, active.Frame.Generation, active.Run.Revision, operation, TenantWorkspaceMutationContract.ActionHash(active.Run, parsed.ParametersHash)),
                new(f.Ids.NewId(), setup.Tenant, active.Run.Owner.ExecutorPrincipalId!.Value, TenantWorkspaceEffectRules.Dispatch, TenantWorkspaceRules.Scope(TenantWorkspaceEffectRules.Owner(active.Run, parsed.Path!)).ResourceId!, "allowed", "workspace_mutation_dispatched", f.Clock.GetUtcNow()));
            Assert.NotNull(start?.Fence); return start?.Receipt;
        }
        if (revokeWorker)
        {
            await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, workerEffects!.AssignmentId, await Revision());
            var scope = artifact ? TenantSessionRules.Scope(setup.Tenant, active.Run.SessionId) : TenantWorkspaceRules.Scope(TenantWorkspaceRules.ForRun(active.Run, "/home"));
            Assert.True((await f.Authorizer.CheckAsync(setup.Agent.PrincipalId, artifact ? "artifacts.create" : "workspaces.write", scope)).Allowed);
            Assert.False((await f.Authorizer.CheckAsync(target.PrincipalId, artifact ? "artifacts.create" : "workspaces.write", scope)).Allowed);
            var deniedRevision = await Revision(); await Assert.ThrowsAsync<AccessDeniedException>(() => Commit());
            Assert.Equal(deniedRevision, await Revision());
            Assert.Equal(active.Run.Revision, (await runs.FindTenantRunAsync(setup.Tenant, active.Run.AgentRunId))!.Revision);
            Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == (artifact ? TenantArtifactOperationRules.AuditAction : TenantWorkspaceEffectRules.Dispatch));
            await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, target.PrincipalId, workerEffectRole!.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        }
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, parentRead.AssignmentId, await Revision());
        var revision = await Revision();
        var refusal = await Assert.ThrowsAsync<AccessDeniedException>(() => Commit()); Assert.Equal("resource_unavailable", refusal.ReasonCode);
        Assert.Equal(revision, await Revision());
        Assert.Equal(active.Run.Revision, (await runs.FindTenantRunAsync(setup.Tenant, active.Run.AgentRunId))!.Revision);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == (artifact ? TenantArtifactOperationRules.AuditAction : TenantWorkspaceEffectRules.Dispatch));
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, active.Run.SessionId, null, null, 100)).Items);
        // Restoring only root read makes this same otherwise-valid frame/operation admissible.
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, requester.PrincipalId, read.RoleId, TenantSessionRules.Scope(setup.Tenant, setup.Session), await Revision());
        var saved = await Commit(); if (artifact) Assert.NotNull(saved);
        Assert.False((await f.Authorizer.CheckAsync(requester.PrincipalId, "sessions.read", TenantSessionRules.Scope(setup.Tenant, child.Run.SessionId))).Allowed);
    }
}
