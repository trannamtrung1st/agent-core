using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_model_read_uses_bound_actual_source_and_current_independent_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, attachmentAuthority: true); var v = await ContextCase(f);
        var read = await ContextService(f).InspectReadAsync(new(v.Run.Owner, v.Run.SessionId, v.Run.AgentRunId, v.Run.Claim!.Generation),
            JsonSerializer.Serialize(new { attachmentId = v.Id }), true, ToolLimits.MaxOutputBytes);
        using var doc = JsonDocument.Parse(read.Text);
        Assert.Equal("Private attachment body", doc.RootElement.GetProperty("inspection").GetProperty("content").GetString());
        Assert.True(doc.RootElement.GetProperty("tenantAttachmentRead").GetBoolean()); Assert.Null(read.Parts);
        var messages = new[] { new ModelMessage(ModelRole.Tool, read.Text, ToolCallId: "read-source", Name: ToolCatalog.AttachmentsRead) };
        await ContextService(f).RequireReadResultsAsync(v.Run, messages);
    }
    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "attachments.read")] [InlineData(true, "attachments.read")]
    [InlineData(false, "capability")] [InlineData(true, "capability")]
    public async Task Tenant_attachment_model_read_denies_missing_independent_reads_or_admitted_capability_before_content_IO(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite, attachmentAuthority: true);
        var v = await ContextCase(f, new[] { "sessions.read", "attachments.read" }.Where(p => p != boundary).ToArray(), denyReadCapability: boundary == "capability");
        var probe = new ContextFileProbe((ITenantAttachmentStore)f.Attachments);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ContextService(f, probe).InspectReadAsync(
            new(v.Run.Owner, v.Run.SessionId, v.Run.AgentRunId, v.Run.Claim!.Generation), JsonSerializer.Serialize(new { attachmentId = v.Id }), true, ToolLimits.MaxOutputBytes).AsTask());
        Assert.Equal(0, probe.Reads);
    }
    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "attachments.read")] [InlineData(true, "attachments.read")]
    public async Task Tenant_attachment_model_read_required_review_refuses_before_source_bytes_without_creating_consent(bool sqlite, string permission)
    {
        await using var f = await Fixture.Create(sqlite, attachmentAuthority: true); var v = await ContextCase(f);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(v.Setup.Tenant, v.Setup.Owner, permission, new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(v.Setup.Tenant))!.Tenant.Revision, 0);
        var probe = new ContextFileProbe((ITenantAttachmentStore)f.Attachments);
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => ContextService(f, probe).InspectReadAsync(
            new(v.Run.Owner, v.Run.SessionId, v.Run.AgentRunId, v.Run.Claim!.Generation), JsonSerializer.Serialize(new { attachmentId = v.Id }), true, ToolLimits.MaxOutputBytes).AsTask());
        Assert.Equal("attachment_read_review_required", denied.ReasonCode); Assert.Equal(0, probe.Reads);
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(v.Setup.Tenant, null, null, new(0, 100))).Items);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_model_read_withholds_body_after_revocation_during_native_read(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, attachmentAuthority: true); var v = await ContextCase(f);
        var probe = new ContextFileProbe((ITenantAttachmentStore)f.Attachments, async () =>
            await f.Access.RevokeAssignmentAsync(v.Setup.Owner.PrincipalId, v.Setup.Tenant, v.Grant.AssignmentId, (await f.Iam.ReadTenantAsync(v.Setup.Tenant))!.Tenant.Revision));
        await Assert.ThrowsAsync<AccessDeniedException>(() => ContextService(f, probe).InspectReadAsync(
            new(v.Run.Owner, v.Run.SessionId, v.Run.AgentRunId, v.Run.Claim!.Generation), JsonSerializer.Serialize(new { attachmentId = v.Id }), true, ToolLimits.MaxOutputBytes).AsTask());
        Assert.Equal(1, probe.Reads);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_model_read_checkpoint_provenance_rechecks_source_and_authority_and_missing_binding_fails_closed(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, attachmentAuthority: true); var v = await ContextCase(f); var reads = ContextService(f);
        var result = await reads.InspectReadAsync(new(v.Run.Owner, v.Run.SessionId, v.Run.AgentRunId, v.Run.Claim!.Generation), JsonSerializer.Serialize(new { attachmentId = v.Id }), true, ToolLimits.MaxOutputBytes);
        var messages = new[] { new ModelMessage(ModelRole.Tool, result.Text, ToolCallId: "read-source", Name: ToolCatalog.AttachmentsRead) };
        var payload = AgentRunToolCallCheckpoint.Write(messages, executionState: new(TranscriptId: v.Run.ResponseId));
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(v.Run.Owner, v.Run.AgentRunId,
            new AgentRunCommand.Checkpoint(v.Run.Revision, f.Clock.GetUtcNow(), v.Run.Claim!.Generation, new(payload, 1, result.Text.Length, 60000), null), f.Ids.NewId());
        Assert.Null(AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).TenantAttachmentSources);
        var bound = new TenantRunExecutionAuthority(AttachmentOperations(f), f.Iam, f.Clock, f.RunConfiguration(), attachmentContext: reads);
        Assert.NotNull(await bound.CurrentDefinitionAsync(run));
        Assert.Null(await new TenantRunExecutionAuthority(AttachmentOperations(f), f.Iam, f.Clock, f.RunConfiguration()).CurrentDefinitionAsync(run));
        var changed = result.Text.Replace("Private source.txt", "Forged source.txt", StringComparison.Ordinal);
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.RequireReadResultsAsync(run, [messages[0] with { Text = changed }]).AsTask());
        var path = Path.Combine(f.Root, "attachments", "tenants", v.Setup.Tenant.ToString("N"), v.Run.SessionId.ToString("N"), v.Id.ToString("N"));
        if (sqlite) { await File.WriteAllTextAsync(path, "changed bytes"); Assert.Null(await bound.CurrentDefinitionAsync(run)); await File.WriteAllTextAsync(path, "Private attachment body"); Assert.NotNull(await bound.CurrentDefinitionAsync(run)); }
        await f.Access.RevokeAssignmentAsync(v.Setup.Owner.PrincipalId, v.Setup.Tenant, v.Grant.AssignmentId, (await f.Iam.ReadTenantAsync(v.Setup.Tenant))!.Tenant.Revision);
        Assert.Null(await bound.CurrentDefinitionAsync(run));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_model_read_typed_tool_boundary_bounds_failures_and_omits_image_parts(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, attachmentAuthority: true); var v = await ContextCase(f);
        var executor = new SessionToolExecutor(tenantAuthorization: new TenantToolAuthorization((ITenantRunStore)f.Iam, AttachmentOperations(f), tenantAttachments: ContextService(f)));
        var result = await executor.ExecuteAsync(v.Run.Admission.Configuration!.Definition, v.Run.SessionId,
            new("missing-source", ToolCatalog.AttachmentsRead, JsonSerializer.Serialize(new { attachmentId = Guid.NewGuid() })), 10,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: v.Run.AgentInstanceId, SupportsVision: true, AgentRunId: v.Run.AgentRunId,
                OwnedSessionId: v.Run.SessionId, TenantId: v.Setup.Tenant, AgentRunGeneration: v.Run.Claim!.Generation));
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(result.Text), 0, 10); Assert.Null(result.Parts);
        using var json = JsonDocument.Parse(result.Text); Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
    }

}
