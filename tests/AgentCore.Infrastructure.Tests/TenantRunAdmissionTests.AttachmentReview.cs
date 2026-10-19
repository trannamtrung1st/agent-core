using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.Synthetic;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private static TenantApprovalService AttachmentApprovals(Fixture f, TenantAttachmentRuntimeContextService reads) => new(f.Iam, (ITenantApprovalStore)f.Iam,
        (ITenantAgentStore)f.Iam, AttachmentOperations(f), f.Authorizer, f.Identity, f.Ids, f.Clock, f.RunConfiguration(), ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default,
        effects: (ITenantApprovedOperationStore)f.Iam, attachmentReads: reads);
    private static TenantApprovedOperationService AttachmentEffects(Fixture f, TenantAttachmentRuntimeContextService reads) => new(f.Iam, (ITenantRunStore)f.Iam,
        (ITenantAgentStore)f.Iam, (ITenantApprovalStore)f.Iam, (ITenantApprovedOperationStore)f.Iam, AttachmentOperations(f), f.RunConfiguration(), f.Ids, f.Clock,
        ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, attachmentReads: reads);
    [Theory] [InlineData(false, "attachments.read")] [InlineData(true, "attachments.read")]
    [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    public async Task Tenant_attachment_read_Review_binds_exact_source_and_returns_one_authorized_body_without_a_grant(bool sqlite, string permission)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, attachmentAuthority: true); var v = await ContextCase(f); var s = v.Setup;
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(s.Tenant, s.Owner, permission, new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam; var probe = new ContextFileProbe((ITenantAttachmentStore)f.Attachments); var reads = ContextService(f, probe); var approvals = AttachmentApprovals(f, reads);
        var call = new ModelToolCall("read-exact", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "read_attachment", read = new { attachmentId = v.Id } }));
        var run = await runtime.ApplyTenantRuntimeCommandAsync(v.Run.Owner, v.Run.AgentRunId, new AgentRunCommand.Checkpoint(v.Run.Revision, f.Clock.GetUtcNow(), v.Run.Claim!.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])], executionState: new(TranscriptId: v.Run.ResponseId)), 1, 0, 60000, 100), null), f.Ids.NewId());
        var prepared = await new TenantToolAuthorization(runs, AttachmentOperations(f), approvals, f.RunConfiguration(), tenantAttachments: reads).PrepareAsync(run.SessionId, call,
            new(false, TriggerKind.UserTurn, AgentInstanceId: run.AgentInstanceId, OwnedSessionId: run.SessionId, AgentRunId: run.AgentRunId, TenantId: s.Tenant, AgentRunGeneration: run.Claim!.Generation), default);
        Assert.Equal(0, probe.Reads); var request = Assert.IsType<TenantApprovalRequest>(prepared.Request); Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal("attachments.read", request.Permission);
        Assert.Equal(v.Id, request.Operation.AttachmentRead!.AttachmentId); Assert.DoesNotContain("Private attachment body", JsonSerializer.Serialize(request));
        Assert.Single((await approvals.ListAsync(s.Tenant, s.Owner, new(0, 100, TenantApprovalStatus.Pending))).Items);
        var view = await approvals.ReadViewAsync(s.Tenant, request.RequestId, s.Owner); Assert.True(view.Review!.CanDecide);
        await approvals.DecideAsync(s.Tenant, request.RequestId, s.Owner, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Equal(0, probe.Reads); Assert.Equal(1, await approvals.DispatchPendingAsync()); var source = (await runs.FindTenantRunAsync(s.Tenant, run.AgentRunId))!;
        var next = (await runs.FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var result = await new TenantRunContinuationService(runs, (ITenantApprovalStore)f.Iam, AttachmentEffects(f, reads), f.Clock, runtime, f.Ids, attachmentReads: reads).ResumeAsync(next);
        Assert.Equal(TenantOperationOutcome.Completed, result.Outcome!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(result.Run.Checkpoint, out var messages)); var tool = Assert.Single(messages!, m => m.Role == ModelRole.Tool);
        using var doc = JsonDocument.Parse(tool.Text); Assert.True(doc.RootElement.GetProperty("readAuthorized").GetBoolean());
        Assert.Equal("Private attachment body", doc.RootElement.GetProperty("attachmentObservation").GetProperty("inspection").GetProperty("content").GetString());
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId)); await reads.RequireStoredAsync(result.Run);
        Assert.Null(await new TenantRunExecutionAuthority(AttachmentOperations(f), f.Iam, f.Clock, f.RunConfiguration()).CurrentDefinitionAsync(result.Run));
        Assert.Single(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == TenantAttachmentReadRules.AuditAction);
        var again = await new TenantRunContinuationService(runs, (ITenantApprovalStore)f.Iam, AttachmentEffects(f, reads), f.Clock, runtime, f.Ids, attachmentReads: reads).ResumeAsync(result.Run);
        Assert.Equal(result.Run.Checkpoint!.PayloadJson, again.Run.Checkpoint!.PayloadJson); Assert.Equal(result.Run.Revision, again.Run.Revision);
    }
}
