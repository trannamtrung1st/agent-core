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
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Background_file_effect_transactions_repeat_actual_root_requester_access_and_ignore_unrelated_child_read(bool sqlite, bool artifact)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var setup = await Setup(f, "general-assistant", 32);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var requester = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        var chat = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Exact chat", ["agents.chat"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, requester.PrincipalId, chat.RoleId, AccessScope.ForResource(setup.Tenant, "agent", setup.Agent.Instance.InstanceId.ToString("D")), await Revision());
        var read = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Root conversation only", ["sessions.read"], await Revision());
        var parentRead = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, requester.PrincipalId, read.RoleId, TenantSessionRules.Scope(setup.Tenant, setup.Session), await Revision());
        var executor = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Independent background file authority",
            ["sessions.create", "agent-runs.create", "agents.read", "sessions.read", "workspaces.read", "workspaces.write", "artifacts.create"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Agent.PrincipalId, executor.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        var intent = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Independent primary intent authority", ["sessions.create", "agent-runs.create", "workspaces.write", "artifacts.create"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, requester.PrincipalId, intent.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, requester, f.Ids.NewId(), "Owned file task", await Revision(), 1);
        var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var parent = await ClaimBackground(f, (await runs.FindTenantRunAsync(setup.Tenant, receipt.RunId))!);
        var seed = new BackgroundSetup(setup, parent.Run, parent.Snapshot, parent.Frame, null); var child = await BackgroundChild(f, seed);
        await runtime.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var active = await ClaimBackground(f, child.Run);
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
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, parentRead.AssignmentId, await Revision());
        async Task<TenantOperationReceipt?> Commit()
        {
            var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!; var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
            if (artifact)
            {
                var args = TenantWorkspaceArtifactContract.Parse("""{"path":"/home/transaction-source.txt","displayName":"transaction.txt"}""");
                var commit = new TenantArtifactCommit(f.Ids.NewId(), active.Run.AgentRunId, active.Frame.Generation, active.Run.Revision, "background-artifact-boundary", args.CanonicalArguments, args.ParametersHash,
                    setup.Agent.Instance.Revision, TenantArtifactToolContract.ActionHash(active.Run.SessionId, args.ParametersHash), TenantArtifactSourceRules.Witness(captured.Observation), captured);
                return await ((ITenantArtifactOperationStore)f.Iam).TryCommitArtifactAsync(next, state.Tenant.Revision, commit,
                    new(f.Ids.NewId(), setup.Tenant, setup.Agent.PrincipalId, TenantArtifactOperationRules.AuditAction, active.Run.SessionId.ToString("D"), "allowed", "operation_completed", f.Clock.GetUtcNow()));
            }
            var parsed = TenantWorkspaceMutationContract.Parse(active.Run.SessionId, ToolCatalog.WorkspaceWrite, """{"path":"/home/transaction.txt","content":"Exact file"}""");
            var operation = new AgentRunDirectOperation(f.Ids.NewId(), "background-file-boundary", ToolCatalog.WorkspaceWrite, parsed.CanonicalArguments, parsed.ParametersHash, setup.Agent.Instance.Revision, setup.Agent.Instance.PersonaRevision);
            var start = await ((ITenantWorkspaceOperationStore)f.Iam).TryStartWorkspaceEffectAsync(next, state.Tenant.Revision,
                new(active.Run.AgentRunId, active.Frame.Generation, active.Run.Revision, operation, TenantWorkspaceMutationContract.ActionHash(active.Run, parsed.ParametersHash)),
                new(f.Ids.NewId(), setup.Tenant, setup.Agent.PrincipalId, TenantWorkspaceEffectRules.Dispatch, TenantWorkspaceRules.Scope(TenantWorkspaceEffectRules.Owner(active.Run, parsed.Path!)).ResourceId!, "allowed", "workspace_mutation_dispatched", f.Clock.GetUtcNow()));
            Assert.NotNull(start?.Fence); return start?.Receipt;
        }
        var revision = await Revision();
        var refusal = await Assert.ThrowsAsync<AccessDeniedException>(() => Commit()); Assert.Equal("requester_unavailable", refusal.ReasonCode);
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
