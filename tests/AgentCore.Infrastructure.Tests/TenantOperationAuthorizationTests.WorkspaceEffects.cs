using System.Text;
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
    private static TenantWorkspaceMutationEffectService WorkspaceEffects(Fixture f, ITenantWorkspaceMutationStore? store = null, ITenantWorkspaceOperationStore? effects = null) => new(Operations(f), f.Iam,
        (ITenantRunStore)f.Iam, (ITenantSessionStore)f.Iam, f.RunConfiguration(), f.Authorizer, effects ?? (ITenantWorkspaceOperationStore)f.Iam, store ?? WorkspaceReader(f), f.Ids, f.Clock, (ITenantApprovalStore)f.Iam,
        (ITenantApprovedOperationStore)f.Iam, MutationTargets(f));
    private static async Task<AgentRun> WorkspaceEffectCheckpoint(Fixture f, TenantRuntimeFrame frame, ModelToolCall call)
    {
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(frame.Owner.TenantId!.Value, frame.RunId))!;
        return await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 100000, 100), null), f.Ids.NewId());
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_effects_dispatch_actual_mutations_once_and_keep_exact_body_free_receipts(bool sqlite, bool working)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32); var frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read", "workspaces.read", "workspaces.write", "workspaces.delete"]);
        var service = WorkspaceEffects(f); var root = working ? "/working" : "/home"; var files = WorkspaceReader(f);
        async Task<TenantWorkspaceMutationResult> Execute(string id, string tool, object arguments)
        {
            var call = new ModelToolCall(id, tool, JsonSerializer.Serialize(arguments)); await WorkspaceEffectCheckpoint(f, frame, call);
            var result = await service.ExecuteAsync(frame, call); Assert.NotNull(result.Receipt); Assert.Equal(TenantOperationOutcome.Completed, result.Receipt.Outcome);
            Assert.Equal(result, await service.ExecuteAsync(frame, call));
            Assert.DoesNotContain("private literal", JsonSerializer.Serialize(result));
            return result;
        }
        await Execute("workspace-write", ToolCatalog.WorkspaceWrite, new { path = root + "/source.txt", content = "private literal: thử nghiệm" });
        await Execute("workspace-patch", ToolCatalog.WorkspacePatch, new { path = root + "/source.txt", edits = new[] { new { oldText = "private literal", newText = "edited literal" } } });
        await Execute("workspace-mkdir", ToolCatalog.WorkspaceMkdir, new { path = root + "/tree/empty" });
        await Execute("workspace-copy", ToolCatalog.WorkspaceCopy, new { source = root + "/source.txt", destination = root + "/tree/copy.txt" });
        var other = working ? "/home" : "/working";
        await Execute("workspace-cross-copy", ToolCatalog.WorkspaceCopy, new { source = root + "/tree", destination = other + "/cross-root" });
        var owner = new TenantWorkspaceReference(s.Tenant, s.Agent.Instance.InstanceId, working ? null : frame.SessionId);
        Assert.Equal("edited literal: thử nghiệm", Encoding.UTF8.GetString((await files.InspectTenantAsync(owner, other + "/cross-root/copy.txt", 1024)).Bytes));
        Assert.Contains((await files.ListTenantAsync(owner, other + "/cross-root", null, 100)).Items, i => i.Directory && i.LogicalPath.EndsWith("/empty"));
        var denied = new ModelToolCall("workspace-sensitive-delete", ToolCatalog.WorkspaceDelete, JsonSerializer.Serialize(new { path = root + "/source.txt" }));
        await WorkspaceEffectCheckpoint(f, frame, denied); var needs = await service.ExecuteAsync(frame, denied);
        Assert.Equal(OperationDecisionKind.RequiresApproval, needs.Decision); Assert.Null(needs.Receipt);
        Assert.NotEmpty((await files.InspectTenantAsync(TenantWorkspaceEffectRules.Owner((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!, root + "/source.txt"), root + "/source.txt", 1024)).Bytes);
        var audit = await f.Iam.ReadAuditAsync(s.Tenant, 200); Assert.Equal(5, audit.Count(a => a.Action == TenantWorkspaceEffectRules.Dispatch)); Assert.Equal(5, audit.Count(a => a.Action == TenantWorkspaceEffectRules.Result));
    }
}
