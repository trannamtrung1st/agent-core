using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_Session_scoped_creator_gets_factual_identity_without_child_read_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, attachmentAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var creator = await User(f, s, "session-attachment-creator@example.test", ["sessions.read"]);
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Session attachment creator", ["attachments.create"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, creator.PrincipalId, role.RoleId, TenantSessionRules.Scope(s.Tenant, s.Run.SessionId), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Run.SessionId))!;
        var service = AttachmentService(f); using var content = new MemoryStream("Creator-only bytes"u8.ToArray());
        var id = await service.UploadAsync(s.Tenant, s.Run.SessionId, creator, f.Ids.NewId(), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, session.Snapshot.Revision, "source.txt", "text/plain", content);
        Assert.NotEqual(Guid.Empty, id); Assert.Empty((await service.ListAsync(s.Tenant, s.Run.SessionId, creator)).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadContentAsync(s.Tenant, s.Run.SessionId, id, creator).AsTask());
        Assert.Equal("Creator-only bytes", System.Text.Encoding.UTF8.GetString((await service.ReadContentAsync(s.Tenant, s.Run.SessionId, id, s.Owner)).Bytes));
    }

    private sealed class AttachmentIntakeProbe(byte[] bytes, Func<Task>? after = null) : MemoryStream(bytes)
    {
        public int Reads { get; private set; }
        private bool fired;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Reads++; var count = await base.ReadAsync(buffer, ct);
            if (!fired && count > 0 && after is not null) { fired = true; await after(); }
            return count;
        }
    }
    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "attachments.create")] [InlineData(true, "attachments.create")]
    public async Task Tenant_attachment_upload_needs_independent_parent_and_creation_authority_before_intake(bool sqlite, string missing)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, attachmentAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var human = await User(f, s, "attachment-creator@example.test", new[] { "sessions.read", "attachments.create" }.Where(k => k != missing).ToArray());
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Run.SessionId))!; using var body = new AttachmentIntakeProbe("Private uploaded body"u8.ToArray());
        await Assert.ThrowsAsync<AccessDeniedException>(() => AttachmentService(f).UploadAsync(s.Tenant, s.Run.SessionId, human, f.Ids.NewId(),
            (f.Iam.ReadTenantAsync(s.Tenant).Result)!.Tenant.Revision, session.Snapshot.Revision, "source.txt", "text/plain", body).AsTask());
        Assert.Equal(0, body.Reads); Assert.Empty((await ((ITenantAttachmentStore)f.Attachments).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 10)).Items);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_attachment_upload_rechecks_current_login_and_IAM_after_intake_before_commit(bool sqlite, bool logout)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, attachmentAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var human = await User(f, s, "attachment-intake-creator@example.test", ["sessions.read", "attachments.create"]); var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var assignment = state.Assignments.Single(a => a.PrincipalId == human.PrincipalId); var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Run.SessionId))!;
        using var body = new AttachmentIntakeProbe("Private uploaded body"u8.ToArray(), async () =>
        {
            if (logout) await f.Identity.SignOutAsync(human.AuthenticationSessionId);
            else await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        });
        var error = await Record.ExceptionAsync(() => AttachmentService(f).UploadAsync(s.Tenant, s.Run.SessionId, human, f.Ids.NewId(), state.Tenant.Revision,
            session.Snapshot.Revision, "source.txt", "text/plain", body).AsTask());
        Assert.True(error is AccessDeniedException or SecurityConflictException, error?.ToString()); Assert.True(body.Reads > 0);
        Assert.Empty((await ((ITenantAttachmentStore)f.Attachments).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 10)).Items);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == "attachments.create");
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_archived_Session_keeps_reads_but_refuses_upload_and_abort(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, attachmentAuthority: true); var s = await Setup(f, "general-assistant", 32); var id = await UploadAttachment(f, s);
        await f.RunService().CancelAsync(s.Tenant, s.Run.AgentRunId, s.Owner, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!.Revision);
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Run.SessionId))!;
        await f.Sessions.ChangeAsync(s.Tenant, s.Owner, new(s.Run.SessionId, session.Snapshot.Revision, TenantSessionAction.Archive), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.NotEmpty((await AttachmentService(f).ReadContentAsync(s.Tenant, s.Run.SessionId, id, s.Owner)).Bytes);
        await Assert.ThrowsAsync<SecurityConflictException>(() => UploadAttachment(f, s));
        await Assert.ThrowsAsync<SecurityConflictException>(() => AttachmentService(f).AbortAsync(s.Tenant, s.Run.SessionId, id, s.Owner, f.Ids.NewId(), (f.Iam.ReadTenantAsync(s.Tenant).Result)!.Tenant.Revision, session.Snapshot.Revision).AsTask());
    }
}
