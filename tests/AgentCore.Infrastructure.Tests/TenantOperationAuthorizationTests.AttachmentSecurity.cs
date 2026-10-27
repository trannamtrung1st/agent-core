using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static TenantAttachmentService AttachmentService(Fixture f, ITenantAttachmentStore? files = null, ITenantAttachmentMutationStore? mutations = null) => new(f.Iam, (ITenantSessionStore)f.Iam, (ITenantAgentStore)f.Iam,
        files ?? (ITenantAttachmentStore)f.Attachments, mutations ?? (ITenantAttachmentMutationStore)f.Iam, f.Identity, f.Authorizer, f.Ids, f.Clock);
    private static async Task<Guid> UploadAttachment(Fixture f, Environment s, string name = "source.txt", string text = "Private attachment content")
    {
        var saved = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Run.SessionId))!;
        return await AttachmentService(f).UploadAsync(s.Tenant, s.Run.SessionId, s.Owner, f.Ids.NewId(), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision,
            saved.Snapshot.Revision, name, "text/plain", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_human_reads_filter_scoped_children_before_paging_and_reject_same_Account_cross_tenant(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, attachmentAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var first = await UploadAttachment(f, s, "first.txt"); var hidden = await UploadAttachment(f, s, "hidden.txt"); var third = await UploadAttachment(f, s, "third.txt");
        var reader = await User(f, s, "attachment-reader@example.test", ["sessions.read"]); var service = AttachmentService(f);
        var empty = await service.ListAsync(s.Tenant, s.Run.SessionId, reader, limit: 1); Assert.Empty(empty.Items); Assert.False(empty.HasMore); Assert.Null(empty.NextCursor);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadContentAsync(s.Tenant, s.Run.SessionId, first, reader).AsTask());
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Exact attachment reader", ["attachments.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        foreach (var id in new[] { first, third }) await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reader.PrincipalId, role.RoleId, TenantAttachmentRules.Scope(s.Tenant, id), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var page = await service.ListAsync(s.Tenant, s.Run.SessionId, reader, limit: 1); var firstVisible = Assert.Single(page.Items).AttachmentId; Assert.Contains(firstVisible, new[] { first, third }); Assert.True(page.HasMore);
        var next = await service.ListAsync(s.Tenant, s.Run.SessionId, reader, page.NextCursor, 1); var secondVisible = Assert.Single(next.Items).AttachmentId; Assert.Contains(secondVisible, new[] { first, third }); Assert.NotEqual(firstVisible, secondVisible); Assert.False(next.HasMore);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(s.Tenant, s.Run.SessionId, reader, hidden, 1).AsTask());
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == s.Owner.AccountId);
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, s.Owner.AccountId, "Beta", "beta"); var betaHuman = s.Owner with { PrincipalId = beta.Users.Single().PrincipalId };
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadContentAsync(beta.Tenant.TenantId, s.Run.SessionId, first, betaHuman).AsTask());
        var betaRole = await f.Access.SaveRoleAsync(betaHuman.PrincipalId, beta.Tenant.TenantId, null, "Beta attachment reader", ["attachments.read"], beta.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Access.AssignRoleAsync(betaHuman.PrincipalId, beta.Tenant.TenantId, betaHuman.PrincipalId, betaRole.RoleId,
            TenantAttachmentRules.Scope(beta.Tenant.TenantId, first), (f.Iam.ReadTenantAsync(beta.Tenant.TenantId).Result)!.Tenant.Revision).AsTask());
        Assert.Equal("Private attachment content", System.Text.Encoding.UTF8.GetString((await service.ReadContentAsync(s.Tenant, s.Run.SessionId, first, s.Owner)).Bytes));
    }

    private sealed class AttachmentReadProbe(ITenantAttachmentStore inner, Func<Task> after) : ITenantAttachmentStore
    {
        public ValueTask<TenantAttachmentUpload> PrepareUploadTenantAsync(TenantSessionRoot owner, Guid id, Guid request, Guid actor, string name, string type, Stream content, CancellationToken ct = default) => inner.PrepareUploadTenantAsync(owner, id, request, actor, name, type, content, ct);
        public ValueTask<bool> OwnsTenantAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default) => inner.OwnsTenantAsync(tenant, session, attachment, ct);
        public ValueTask<AttachmentRecord?> FindTenantAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default) => inner.FindTenantAsync(tenant, session, attachment, ct);
        public ValueTask<TenantAttachmentPage> ListTenantAsync(Guid tenant, Guid session, IReadOnlyList<Guid>? allowed, Guid? before, int limit, CancellationToken ct = default, bool boundOnly = false) => inner.ListTenantAsync(tenant, session, allowed, before, limit, ct, boundOnly);
        public async ValueTask<byte[]> ReadTenantContentAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default) { var bytes = await inner.ReadTenantContentAsync(tenant, session, attachment, ct); await after(); return bytes; }
    }
    [Theory] [InlineData(false, "revoke")] [InlineData(true, "revoke")] [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "expire")] [InlineData(true, "expire")]
    public async Task Tenant_attachment_read_withholds_bytes_after_revoke_logout_or_pending_expiry(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1)); await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true, attachmentAuthority: true); var s = await Setup(f, "general-assistant", 35); var id = await UploadAttachment(f, s);
        var reader = await User(f, s, "attachment-data-reader@example.test", ["sessions.read", "attachments.read"]);
        var assignment = (await f.Iam.ReadTenantAsync(s.Tenant))!.Assignments.Single(a => a.PrincipalId == reader.PrincipalId);
        var probe = new AttachmentReadProbe((ITenantAttachmentStore)f.Attachments, async () =>
        {
            if (boundary == "expire") clock.Advance(TimeSpan.FromMinutes(61));
            else if (boundary == "logout") await f.Identity.SignOutAsync(reader.AuthenticationSessionId);
            else await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        });
        var error = await Record.ExceptionAsync(() => AttachmentService(f, probe).ReadContentAsync(s.Tenant, s.Run.SessionId, id, reader).AsTask());
        Assert.True(error is AccessDeniedException or SecurityConflictException, error?.ToString());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_expiry_and_Session_deletion_cleanup_are_owned_durable_and_retire_child_grants(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1)); await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true, attachmentAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var expired = await UploadAttachment(f, s); var files = (ITenantAttachmentStore)f.Attachments; var cleanup = (ITenantAttachmentCleanupStore)f.Attachments;
        clock.Advance(TimeSpan.FromMinutes(61)); var targets = await cleanup.ListAttachmentCleanupAsync(null, 100); Assert.Contains(targets.Items, t => t.AttachmentId == expired);
        var revision = (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        await Assert.ThrowsAsync<AccessDeniedException>(() => AttachmentService(f).AbortAsync(s.Tenant, s.Run.SessionId, expired, s.Owner, f.Ids.NewId(), revision,
            ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Run.SessionId).Result!.Snapshot.Revision).AsTask());
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.False(await cleanup.CleanupAttachmentContentAsync(new(Guid.NewGuid(), s.Run.SessionId, expired, new string('a', 64))));
        Assert.True(await new TenantAttachmentCleanupService(cleanup).RecoverAsync() >= 1); Assert.Empty((await files.ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 10)).Items);
        var active = await UploadAttachment(f, s, "active.txt"); var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Attachment scoped reader", ["attachments.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var assignment = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Owner.PrincipalId, role.RoleId, TenantAttachmentRules.Scope(s.Tenant, active), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.False(await cleanup.CleanupAttachmentContentAsync(new(s.Tenant, s.Run.SessionId, active, (await files.FindTenantAsync(s.Tenant, s.Run.SessionId, active))!.Sha256Hex)));
        await f.RunService().CancelAsync(s.Tenant, s.Run.AgentRunId, s.Owner, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!.Revision);
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Run.SessionId))!;
        await f.Sessions.ChangeAsync(s.Tenant, s.Owner, new(s.Run.SessionId, session.Snapshot.Revision, TenantSessionAction.Delete), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.DoesNotContain((await f.Iam.ReadTenantAsync(s.Tenant))!.Assignments, a => a.AssignmentId == assignment.AssignmentId);
        Assert.True(await new TenantAttachmentCleanupService(cleanup).RecoverAsync() >= 1); Assert.Null(await files.FindTenantAsync(s.Tenant, s.Run.SessionId, active));
        if (sqlite) Assert.False(Directory.Exists(Path.Combine(f.Root, "attachments", "tenants", s.Tenant.ToString("N"), s.Run.SessionId.ToString("N"))));
        Assert.DoesNotContain("Private attachment content", JsonSerializer.Serialize(await f.Iam.ReadAuditAsync(s.Tenant, 200)));
    }
}
