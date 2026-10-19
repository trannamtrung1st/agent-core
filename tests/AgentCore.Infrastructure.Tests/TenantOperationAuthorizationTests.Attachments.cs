using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static TenantAttachmentMutation AttachmentProof(Fixture f, Environment s, TenantSession session, long revision, Guid request, string action, Guid? target = null) =>
        new(session.Owner, s.Owner, revision, session.Snapshot.Revision, s.Agent.Instance.Revision, request,
            new(request, s.Tenant, s.Owner.PrincipalId, action, (target ?? session.Owner.SessionId).ToString("D"), "allowed", "human_attachment_authorized", f.Clock.GetUtcNow()));

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_storage_upload_read_retry_abort_preserves_exact_actual_owner(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, attachmentAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Run.SessionId))!;
        var files = (ITenantAttachmentStore)f.Attachments; var mutations = (ITenantAttachmentMutationStore)f.Iam;
        var request = f.Ids.NewId(); var bytes = "Exact uploaded attachment: thử nghiệm"u8.ToArray();
        var upload = await files.PrepareUploadTenantAsync(session.Owner, f.Ids.NewId(), request, s.Owner.PrincipalId, "source.txt", "text/plain", new MemoryStream(bytes));
        var proof = AttachmentProof(f, s, session, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, request, "attachments.create");
        var saved = await mutations.TryUploadAttachmentAsync(proof, upload); Assert.NotNull(saved); Assert.Equal(s.Tenant, saved.TenantId);
        Assert.Equal(session.Owner.SessionId, saved.SessionId); Assert.Equal(s.Owner.PrincipalId, saved.UploadedByPrincipalId);
        Assert.Equal(bytes, await files.ReadTenantContentAsync(s.Tenant, saved.SessionId, saved.AttachmentId));
        Assert.Equal(saved, await mutations.TryUploadAttachmentAsync(proof, upload));
        Assert.Single((await files.ListTenantAsync(s.Tenant, saved.SessionId, null, null, 10)).Items);
        Assert.Null(await files.FindTenantAsync(Guid.NewGuid(), saved.SessionId, saved.AttachmentId));
        Assert.Null(await files.FindTenantAsync(s.Tenant, Guid.NewGuid(), saved.AttachmentId));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Attachments.GetAsync(saved.SessionId, saved.AttachmentId).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Attachments.ListForSessionAsync(saved.SessionId).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Attachments.OpenContentAsync(saved.SessionId, saved.AttachmentId).AsTask());
        var abort = AttachmentProof(f, s, session, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, f.Ids.NewId(), "attachments.delete", saved.AttachmentId);
        Assert.True(await mutations.TryAbortAttachmentAsync(abort, saved.AttachmentId));
        Assert.True(await mutations.TryAbortAttachmentAsync(abort, saved.AttachmentId));
        Assert.Empty((await files.ListTenantAsync(s.Tenant, saved.SessionId, null, null, 10)).Items); Assert.Null(await files.FindTenantAsync(s.Tenant, saved.SessionId, saved.AttachmentId));
        Assert.DoesNotContain("Exact uploaded", JsonSerializer.Serialize(await f.Iam.ReadAuditAsync(s.Tenant, 200)));
    }
}
