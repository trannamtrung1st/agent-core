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
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Artifact_source_consent_pending_checkpoint_resumes_one_actual_successor_with_a_durable_result(bool sqlite, bool grant)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s, grant ? ["sessions.read"] : null); await GrantWorkspaceRuntimeReads(f, s);
        byte[] bytes = [0, 255, 128, 3]; await SeedRuntimeWorkspace(f, s, true, "source.bin", bytes);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "workspaces.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var call = new ModelToolCall("exact-source-consent", ToolCatalog.RequestAuthorization,
            """{"operation":"publish_workspace_artifact","artifact":{"path":"/working/source.bin","displayName":"published.bin"}}""");
        var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam; var run = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 10000, 100), null), f.Ids.NewId());
        var approvals = ApprovalService(f, artifactSources: ArtifactSources(f));
        var prepared = await new TenantToolAuthorization(runs, Operations(f), f.Clock, approvals, f.RunConfiguration()).PrepareAsync(run.SessionId, call,
            new(false, TriggerKind.UserTurn, AgentInstanceId: s.Agent.Instance.InstanceId, OwnedSessionId: frame.SessionId, SupportsTools: true,
                AgentRunId: frame.RunId, TenantId: s.Tenant, AgentRunGeneration: frame.Generation), default);
        var request = prepared.Request!; Assert.NotNull(request); Assert.Equal(grant ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
        await approvals.DecideAsync(s.Tenant, request.RequestId, s.Owner, true, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        Assert.Equal(1, await approvals.DispatchPendingAsync()); var source = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
        Assert.Equal(AgentRunOutcomeKind.Continued, source.Result!.OutcomeKind); var next = (await runs.FindTenantRunAsync(s.Tenant, source.Result.ContinuationRunId!.Value))!;
        next = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId,
            new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var continuations = new TenantRunContinuationService(runs, (ITenantApprovalStore)f.Iam, Effects(f, artifactSources: ArtifactSources(f)), f.Clock, runtime, f.Ids);
        var result = await continuations.ResumeAsync(next); Assert.Equal(TenantOperationOutcome.Completed, result.Outcome!.Outcome);
        var artifact = Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, next.SessionId, null, null, 20)).Items);
        Assert.Equal(next.AgentRunId, artifact.AgentRunId); Assert.Equal(next.AgentRunId, result.Outcome.RunId); Assert.Equal(frame.Owner, next.Owner);
        Assert.Equal(bytes, await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, next.SessionId, artifact.ArtifactId));
        Assert.True(AgentRunToolCallCheckpoint.TryRead(result.Run.Checkpoint, out var messages)); Assert.Empty(AgentRunToolCallCheckpoint.PendingCalls(messages!));
        using var outcome = JsonDocument.Parse(Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text); Assert.True(outcome.RootElement.GetProperty("applied").GetBoolean());
        Assert.DoesNotContain("source.bin", outcome.RootElement.GetRawText());
        Assert.Equal(0, await approvals.DispatchPendingAsync()); Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, next.SessionId, null, null, 20)).Items);
    }
}
