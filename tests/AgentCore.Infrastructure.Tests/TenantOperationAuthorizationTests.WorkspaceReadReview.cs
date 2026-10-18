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
    [Theory] [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    public Task Workspace_read_Review_returns_only_one_saved_observation_through_the_existing_successor_and_consent_receipt(bool sqlite, bool working, bool list) => WorkspaceReadReviewJourney(sqlite, working, list);
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public Task Tenant_workspace_search_Review_uses_the_saved_traversal_and_existing_successor_receipt(bool sqlite, bool working) => WorkspaceReadReviewJourney(sqlite, working, false, search: true);

    private async Task WorkspaceReadReviewJourney(bool sqlite, bool working, bool list, bool search = false)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var frame = await ClaimArtifactRun(f, s); await SeedRuntimeWorkspace(f, s, working); var grant = await GrantWorkspaceRuntimeReads(f, s);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "workspaces.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam; var reads = WorkspaceRuntimeReads(f);
        var path = working ? "/working" : "/home"; if (!list && !search) path += "/private.txt";
        var tool = search ? ToolCatalog.WorkspaceSearch : list ? ToolCatalog.WorkspaceList : ToolCatalog.WorkspaceRead;
        var json = search ? JsonSerializer.Serialize(new { path, query = "Private", glob = "**/*.txt", maxResults = 10 }) : JsonSerializer.Serialize(new { path });
        var call = new ModelToolCall("inspect-exact-workspace", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "inspect_workspace", read = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(json) } }));
        var run = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 10000, 100), null), f.Ids.NewId());
        var approvals = ApprovalService(f, workspaceReads: reads);
        var prepared = await new TenantToolAuthorization(runs, Operations(f), approvals, f.RunConfiguration(), workspaceReads: reads).PrepareAsync(run.SessionId, call,
            new(false, TriggerKind.UserTurn, AgentInstanceId: s.Agent.Instance.InstanceId, OwnedSessionId: run.SessionId, SupportsTools: true, AgentRunId: run.AgentRunId, TenantId: s.Tenant, AgentRunGeneration: frame.Generation), default);
        var request = Assert.IsType<TenantApprovalRequest>(prepared.Request); Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal(TenantOperationKind.WorkspaceRead, request.Operation.Kind);
        Assert.Equal(working ? "working:" + frame.SessionId.ToString("D") : "home:" + s.Agent.Instance.InstanceId.ToString("D"), request.ResourceId);
        Assert.DoesNotContain("Private model bytes", JsonSerializer.Serialize(request));
        var waiting = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!; Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status); Assert.Null(waiting.Claim);
        var view = await approvals.ReadViewAsync(s.Tenant, request.RequestId, s.Owner); Assert.True(view.Review!.CanDecide);
        Assert.Contains(view.Review.Changes, c => c.Field == "path" && Equals(c.After, path));
        await approvals.DecideAsync(s.Tenant, request.RequestId, s.Owner, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Equal(1, await approvals.DispatchPendingAsync()); var source = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var next = (await runs.FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var continuations = new TenantRunContinuationService(runs, (ITenantApprovalStore)f.Iam, Effects(f, workspaceReads: reads), f.Clock, runtime, f.Ids, workspaceReads: reads);
        var result = await continuations.ResumeAsync(next); Assert.Equal(TenantOperationOutcome.Completed, result.Outcome!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(result.Run.Checkpoint, out var messages)); var text = Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text;
        using var doc = JsonDocument.Parse(text); Assert.True(doc.RootElement.GetProperty("readAuthorized").GetBoolean()); Assert.False(doc.RootElement.GetProperty("applied").GetBoolean());
        var data = doc.RootElement.GetProperty("workspaceObservation"); Assert.True(data.GetProperty("tenantWorkspaceRead").GetBoolean());
        if (search) Assert.Equal(path + "/private.txt", Assert.Single(data.GetProperty("matches").EnumerateArray()).GetProperty("path").GetString());
        else if (list) Assert.Equal(path + "/private.txt", Assert.Single(data.GetProperty("entries").EnumerateArray()).GetProperty("path").GetString());
        else Assert.Equal("Private model bytes: thử nghiệm", data.GetProperty("content").GetString());
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
        Assert.Single(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == TenantWorkspaceReadOperationRules.AuditAction);
        await reads.RequireStoredAsync(result.Run);
        var again = await continuations.ResumeAsync(result.Run); Assert.Equal(result.Run.Checkpoint!.PayloadJson, again.Run.Checkpoint!.PayloadJson); Assert.Equal(result.Run.Revision, again.Run.Revision);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.RequireStoredAsync(result.Run).AsTask());
    }
}
