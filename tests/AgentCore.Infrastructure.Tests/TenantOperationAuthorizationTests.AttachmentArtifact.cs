using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static async Task<(Environment Setup, Guid Id, byte[] Bytes)> AttachmentExportCase(Fixture f)
    {
        var s = await Setup(f, "general-assistant", 32);
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Run.SessionId))!;
        var files = (ITenantAttachmentStore)f.Attachments; var bytes = "Exact original Attachment → thử nghiệm"u8.ToArray(); var request = f.Ids.NewId();
        var upload = await files.PrepareUploadTenantAsync(session.Owner, f.Ids.NewId(), request, s.Owner.PrincipalId, "Private original.txt", "text/plain", new MemoryStream(bytes));
        await ((ITenantAttachmentMutationStore)f.Iam).TryUploadAttachmentAsync(AttachmentProof(f, s, session, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, request, "attachments.create"), upload);
        var sent = await f.RunService().SubmitMessageAsync(s.Tenant, session.Owner.SessionId, s.Owner, f.Ids.NewId(), "Copy this file", [upload.Record.AttachmentId],
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, session.Snapshot.Revision);
        return (s with { Run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, sent.RunId))! }, upload.Record.AttachmentId, bytes);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Attachment_export_copies_actual_bound_original_with_one_receipt_and_independent_download(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true, attachmentAuthority: true);
        var v = await AttachmentExportCase(f); var s = v.Setup; var frame = await ClaimArtifactRun(f, s);
        await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s, ["attachments.read"]);
        var args = JsonSerializer.Serialize(new { path = "/attachments/" + v.Id.ToString("D"), displayName = "published-original.txt" });
        var service = SourcePublicationService(f); var result = await service.ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "attachment-export", args);
        Assert.NotNull(result.Receipt); Assert.Null(result.Receipt.RequestId);
        var artifact = Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Equal(result.Receipt.OperationId, artifact.ArtifactId);
        Assert.Equal(v.Bytes, (await ArtifactService(f, (ITenantArtifactStore)f.Artifacts).ReadContentAsync(s.Tenant, frame.SessionId, artifact.ArtifactId, s.Owner)).Bytes);
        Assert.Equal(result, await service.ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "attachment-export", args));
        Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.False((await f.Authorizer.CheckAsync(s.Agent.PrincipalId, "artifacts.read", TenantArtifactRules.Scope(s.Tenant, artifact.ArtifactId))).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(s.Agent.PrincipalId, "workspaces.read", TenantWorkspaceRules.Scope(new(s.Tenant, s.Agent.Instance.InstanceId, frame.SessionId)))).Allowed);
        Assert.Equal(AttachmentState.Bound, (await ((ITenantAttachmentStore)f.Attachments).FindTenantAsync(s.Tenant, frame.SessionId, v.Id))!.State);
    }
}

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "attachments.read")] [InlineData(true, "attachments.read")]
    public async Task Tenant_Attachment_export_atomic_commit_requires_each_raw_read_after_private_capture(bool sqlite, string missing)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true, attachmentAuthority: true);
        var v = await AttachmentExportCase(f); var s = v.Setup; var frame = await ClaimArtifactRun(f, s);
        await GrantPublication(f, s, ["artifacts.create"]);
        await GrantWorkspaceRuntimeReads(f, s, new[] { "sessions.read", "attachments.read" }.Where(p => p != missing).ToArray());
        var owner = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, frame.SessionId))!.Owner;
        var source = await ((ITenantAttachmentArtifactSourceStore)f.Attachments).CaptureAttachmentArtifactSourceTenantAsync(owner, v.Id);
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!; var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var args = AgentCore.Application.Tools.TenantWorkspaceArtifactContract.Parse(JsonSerializer.Serialize(new { path = source.Observation.Path, displayName = "copy.txt" }));
        var commit = new TenantArtifactCommit(f.Ids.NewId(), frame.RunId, frame.Generation, run.Revision, "raw-attachment-bypass", args.CanonicalArguments, args.ParametersHash,
            s.Agent.Instance.Revision, AgentCore.Application.Tools.TenantArtifactToolContract.ActionHash(frame.SessionId, args.ParametersHash), TenantArtifactSourceRules.Witness(source.Observation), source);
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantArtifactOperationStore)f.Iam).TryCommitArtifactAsync(
            state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, commit,
            new(f.Ids.NewId(), s.Tenant, s.Agent.PrincipalId, TenantArtifactOperationRules.AuditAction, frame.SessionId.ToString("D"), "allowed", "operation_completed", f.Clock.GetUtcNow())).AsTask())).ReasonCode);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Null(await ((ITenantArtifactOperationStore)f.Iam).FindDirectOperationReceiptAsync(s.Tenant, frame.RunId, commit.SourceCallId));
    }
    [Fact]
    public async Task Tenant_Attachment_export_rechecks_native_original_inside_commit_and_recovers_only_after_bytes_are_restored()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true, attachmentAuthority: true); var v = await AttachmentExportCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s, ["attachments.read"]);
        var args = JsonSerializer.Serialize(new { path = "/attachments/" + v.Id.ToString("D"), displayName = "copy.txt" });
        var gate = new ArtifactCommitGate((ITenantArtifactOperationStore)f.Iam); var pending = SourcePublicationService(f, gate).ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "native-original", args).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var native = Path.Combine(f.Root, "attachments", "tenants", s.Tenant.ToString("N"), frame.SessionId.ToString("N"), v.Id.ToString("N"));
        try { await File.WriteAllBytesAsync(native, "Corrupted original"u8.ToArray()); } finally { gate.Proceed.TrySetResult(); }
        Assert.Equal("attachment_source_changed", (await Assert.ThrowsAsync<SecurityConflictException>(async () => await pending)).ReasonCode);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Null(await ((ITenantArtifactOperationStore)f.Iam).FindDirectOperationReceiptAsync(s.Tenant, frame.RunId, "native-original"));
        await File.WriteAllBytesAsync(native, v.Bytes);
        var result = await SourcePublicationService(f).ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "native-original", args);
        Assert.Equal(v.Bytes, await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, frame.SessionId, result.Receipt!.OperationId));
    }
}
