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
    [Theory] [InlineData(false, "artifacts.read")] [InlineData(true, "artifacts.read")]
    [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    public async Task Artifact_verification_Review_releases_source_Run_and_returns_exact_metadata_from_one_successor_consent(bool sqlite, string permission)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Setup(f, "customer-support", 3); var frame = await ClaimArtifactRun(f, s);
        var item = await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(frame, f.Ids.NewId(), "exact.md", "text/markdown", "Undisclosed file body"u8.ToArray());
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Read exact files", ["sessions.read", "artifacts.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var assignment = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, permission, new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam; var reads = ArtifactReads(f);
        var call = new ModelToolCall("verify-exact", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "verify_artifact", artifactId = item.ArtifactId }));
        var run = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 10000, 100), null), f.Ids.NewId());
        var approvals = ApprovalService(f, artifactReads: reads);
        var prepared = await new TenantToolAuthorization(runs, Operations(f), approvals, f.RunConfiguration(), artifactReads: reads).PrepareAsync(run.SessionId, call,
            new(false, TriggerKind.UserTurn, AgentInstanceId: s.Agent.Instance.InstanceId, OwnedSessionId: frame.SessionId, SupportsTools: true,
                AgentRunId: frame.RunId, TenantId: s.Tenant, AgentRunGeneration: frame.Generation), default);
        var request = Assert.IsType<TenantApprovalRequest>(prepared.Request); Assert.Equal(ApprovalKind.Review, request.Kind);
        Assert.Equal("artifacts.read", request.Permission); Assert.Equal(item.ArtifactId.ToString("D"), request.ResourceId);
        Assert.Equal(item.Sha256Hex, request.Operation.ArtifactVerification!.Sha256Hex);
        var waiting = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!; Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status); Assert.Null(waiting.Claim);
        Assert.DoesNotContain("Undisclosed file body", JsonSerializer.Serialize(request));
        var view = await approvals.ReadViewAsync(s.Tenant, request.RequestId, s.Owner); Assert.True(view.Review!.CanDecide);
        Assert.Contains(view.Review.Changes, c => c.Field == "artifactDisplayName" && Equals(c.After, item.DisplayName));
        await approvals.DecideAsync(s.Tenant, request.RequestId, s.Owner, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Equal(1, await approvals.DispatchPendingAsync()); var source = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var next = (await runs.FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId,
            new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var continuations = new TenantRunContinuationService(runs, (ITenantApprovalStore)f.Iam, Effects(f, artifactReads: reads), f.Clock, runtime, f.Ids, artifactReads: reads);
        var result = await continuations.ResumeAsync(next); Assert.Equal(TenantOperationOutcome.Completed, result.Outcome!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(result.Run.Checkpoint, out var messages));
        var text = Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text;
        using var doc = JsonDocument.Parse(text); Assert.True(doc.RootElement.GetProperty("readAuthorized").GetBoolean()); Assert.False(doc.RootElement.GetProperty("applied").GetBoolean());
        Assert.Equal(item.ArtifactId, doc.RootElement.GetProperty("artifactMetadata").GetProperty("artifactId").GetGuid());
        Assert.DoesNotContain("Undisclosed file body", text); Assert.DoesNotContain("workspaceLogicalPath", text);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
        Assert.Single(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == TenantArtifactVerificationRules.AuditAction);
        await reads.RequireStoredAsync(result.Run);
        var again = await continuations.ResumeAsync(result.Run); Assert.Equal(result.Run.Checkpoint!.PayloadJson, again.Run.Checkpoint!.PayloadJson); Assert.Equal(result.Run.Checkpoint.OutputBytes, again.Run.Checkpoint.OutputBytes); Assert.Equal(result.Run.Checkpoint.RemainingOverallBudgetMs, again.Run.Checkpoint.RemainingOverallBudgetMs); Assert.Equal(result.Run.Revision, again.Run.Revision);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.RequireStoredAsync(result.Run).AsTask());
    }
}
