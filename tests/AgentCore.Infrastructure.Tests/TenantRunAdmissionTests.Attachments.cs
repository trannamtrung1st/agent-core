using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_message_attachment_binding_commits_one_entry_Run_and_exact_sources_and_retries_without_rebinding(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, attachmentAuthority: true); var setup = await Setup(f);
        var files = (ITenantAttachmentStore)f.Attachments; var mutations = (ITenantAttachmentMutationStore)f.Iam;
        var attachments = new TenantAttachmentService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantAgentStore)f.Iam, files, mutations, f.Identity, f.Authorizer, f.Ids, f.Clock);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var id = await attachments.UploadAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), await Revision(), 1, "Private nguồn.txt", "text/plain", new MemoryStream("Exact source bytes"u8.ToArray()));
        var original = (await files.FindTenantAsync(setup.Tenant, setup.Session, id))!;
        var message = f.Ids.NewId(); var service = f.RunService();
        var receipt = await service.SubmitMessageAsync(setup.Tenant, setup.Session, setup.Owner, message, "", [id], await Revision(), 1);
        var bound = (await files.FindTenantAsync(setup.Tenant, setup.Session, id))!;
        Assert.Equal(AttachmentState.Bound, bound.State); Assert.Equal(receipt.EntryId, bound.EntryId); Assert.Null(bound.ExpiresAt);
        Assert.Equal(original.Sha256Hex, bound.Sha256Hex); Assert.Equal(original.UploadedByPrincipalId, bound.UploadedByPrincipalId);
        Assert.Equal("Exact source bytes", System.Text.Encoding.UTF8.GetString(await files.ReadTenantContentAsync(setup.Tenant, setup.Session, id)));
        var page = (await ((ITenantConversationStore)f.Iam).ReadTenantConversationAsync(setup.Tenant, setup.Session, 0, null, 10))!;
        var input = Assert.Single(page.History.Items); Assert.Equal(receipt.EntryId, input.EntryId); Assert.Equal(id, Assert.Single(input.Attachments!).AttachmentId);
        var revision = await Revision();
        var retry = await service.SubmitMessageAsync(setup.Tenant, setup.Session, setup.Owner, message, "", [id], revision, receipt.SessionRevision);
        Assert.False(retry.Created); Assert.Equal(receipt.RunId, retry.RunId); Assert.Equal(revision, await Revision()); Assert.Equal(bound, await files.FindTenantAsync(setup.Tenant, setup.Session, id));
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.SubmitMessageAsync(setup.Tenant, setup.Session, setup.Owner, message, "Changed request", [id], revision, receipt.SessionRevision).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SubmitMessageAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Reuse bound source", [id], revision, receipt.SessionRevision).AsTask());
        Assert.Single(await ((ITenantRunStore)f.Iam).ListTenantRunIdsAsync(setup.Tenant, setup.Session));
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext(); var relation = await db.MessageAttachments.SingleAsync();
            Assert.Equal(receipt.EntryId.ToString("D"), relation.EntryId); Assert.Equal(id.ToString("D"), relation.AttachmentId);
            var reopened = new SqliteAttachmentStore(f.Contexts, f.Clock, Path.Combine(f.Root, "attachments")); Assert.Equal(bound, await reopened.FindTenantAsync(setup.Tenant, setup.Session, id));
        }
    }
}
