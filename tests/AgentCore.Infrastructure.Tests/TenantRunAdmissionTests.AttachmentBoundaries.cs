using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private static TenantAttachmentService AttachmentService(Fixture f) => new(f.Iam, (ITenantSessionStore)f.Iam, (ITenantAgentStore)f.Iam,
        (ITenantAttachmentStore)f.Attachments, (ITenantAttachmentMutationStore)f.Iam, f.Identity, f.Authorizer, f.Ids, f.Clock);
    private static async Task<Guid> Upload(Fixture f, Environment s) => await AttachmentService(f).UploadAsync(s.Tenant, s.Session, s.Owner, f.Ids.NewId(),
        (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1, "Private source.txt", "text/plain", new MemoryStream("Private attachment body"u8.ToArray()));

    [Theory] [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "expire")] [InlineData(true, "expire")]
    [InlineData(false, "abort")] [InlineData(true, "abort")]
    public async Task Tenant_message_attachment_binding_rechecks_source_and_original_human_without_partial_graph(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); await using var f = await Fixture.Create(sqlite, clock, attachmentAuthority: true); var setup = await Setup(f);
        var id = await Upload(f, setup); var human = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var chat = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Chat and Session", ["agents.chat", "sessions.read"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, human.PrincipalId, chat.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        var read = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Read exact upload", ["attachments.read"], await Revision());
        var assignment = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, human.PrincipalId, read.RoleId, TenantAttachmentRules.Scope(setup.Tenant, id), await Revision());
        var gate = new Gate((ITenantRunStore)f.Iam); var pending = f.RunService(gate).SubmitMessageAsync(setup.Tenant, setup.Session, human, f.Ids.NewId(), "Read this", [id], await Revision(), 1).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (boundary == "revoke") await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, assignment.AssignmentId, await Revision());
            if (boundary == "logout") await f.Identity.SignOutAsync(human.AuthenticationSessionId);
            if (boundary == "expire") clock.Advance(TimeSpan.FromMinutes(61));
            if (boundary == "abort") await AttachmentService(f).AbortAsync(setup.Tenant, setup.Session, id, setup.Owner, f.Ids.NewId(), await Revision(), 1);
        }
        finally { gate.Proceed.TrySetResult(); }
        var error = await Record.ExceptionAsync(() => pending); Assert.True(error is AccessDeniedException or SecurityConflictException, error?.ToString());
        Assert.Empty(await ((ITenantRunStore)f.Iam).ListTenantRunIdsAsync(setup.Tenant, setup.Session));
        Assert.Equal(1, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision);
        Assert.Empty((await ((ITenantConversationStore)f.Iam).ReadTenantConversationAsync(setup.Tenant, setup.Session, 0, null, 10))!.History.Items);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "agent-runs.admit");
        if (boundary is "revoke" or "logout") Assert.Equal(AttachmentState.Pending, (await ((ITenantAttachmentStore)f.Attachments).FindTenantAsync(setup.Tenant, setup.Session, id))!.State);
        if (sqlite) { await using var db = f.Contexts!.CreateDbContext(); Assert.Empty(await db.MessageAttachments.ToArrayAsync()); Assert.Equal("Pending", (await db.Attachments.SingleAsync()).State); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_message_attachment_binding_requires_independent_child_read_and_refuses_duplicate_or_foreign_sources(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, attachmentAuthority: true); var setup = await Setup(f); var id = await Upload(f, setup);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var human = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        var chat = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Chat and Session", ["agents.chat", "sessions.read"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, human.PrincipalId, chat.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        var service = f.RunService();
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SubmitMessageAsync(setup.Tenant, setup.Session, human, f.Ids.NewId(), "Read", [id], Revision().Result, 1).AsTask());
        foreach (var ids in new[] { new[] { id, id }, Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToArray(), new[] { Guid.Empty } })
            Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => service.SubmitMessageAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Read", ids, Revision().Result, 1).AsTask())).Code);
        var other = await f.Sessions.CreateAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner, SessionMode.Text, await Revision(), 1);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SubmitMessageAsync(setup.Tenant, other.Owner.SessionId, setup.Owner, f.Ids.NewId(), "Read", [id], Revision().Result, 1).AsTask());
        Assert.Equal(AttachmentState.Pending, (await ((ITenantAttachmentStore)f.Attachments).FindTenantAsync(setup.Tenant, setup.Session, id))!.State);
        Assert.Empty(await ((ITenantRunStore)f.Iam).ListTenantRunIdsAsync(setup.Tenant, setup.Session));
    }

    private sealed class AttachmentBindingFault : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<MessageAttachmentRow>().Any(e => e.State == EntityState.Added)) throw new InvalidOperationException("controlled binding publication fault");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class BindingContexts(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
    [Fact]
    public async Task Tenant_message_attachment_binding_metadata_failure_rolls_back_first_save_and_all_graph_source_and_audit_facts()
    {
        await using var f = await Fixture.Create(true, attachmentAuthority: true); var setup = await Setup(f); var id = await Upload(f, setup);
        string connection; await using (var db = f.Contexts!.CreateDbContext()) connection = db.Database.GetDbConnection().ConnectionString;
        var contexts = new BindingContexts(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite(connection).AddInterceptors(new AttachmentBindingFault()).Options);
        var store = new SqliteIamStore(contexts, builtIns: f.BuiltIns, clock: f.Clock, attachmentBacking: (SqliteAttachmentStore)f.Attachments);
        var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision; var message = f.Ids.NewId();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.RunService(store).SubmitMessageAsync(setup.Tenant, setup.Session, setup.Owner, message, "Read", [id], revision, 1).AsTask());
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Empty(await ((ITenantRunStore)f.Iam).ListTenantRunIdsAsync(setup.Tenant, setup.Session));
        Assert.Equal(1, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision);
        Assert.Equal(AttachmentState.Pending, (await ((ITenantAttachmentStore)f.Attachments).FindTenantAsync(setup.Tenant, setup.Session, id))!.State);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "agent-runs.admit");
        await using (var db = f.Contexts!.CreateDbContext()) { Assert.Empty(await db.Entries.ToArrayAsync()); Assert.Empty(await db.Activations.ToArrayAsync()); Assert.Empty(await db.MessageAttachments.ToArrayAsync()); }
        Assert.True((await f.RunService().SubmitMessageAsync(setup.Tenant, setup.Session, setup.Owner, message, "Read", [id], revision, 1)).Created);
    }
}
