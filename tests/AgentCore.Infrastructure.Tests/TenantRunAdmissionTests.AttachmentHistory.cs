using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_message_attachment_history_checks_actual_Session_child_reads_and_canonicalizes_labels_without_granting_bytes(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, attachmentAuthority: true); var setup = await Setup(f); var id = await Upload(f, setup);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var other = await f.Sessions.CreateAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner, SessionMode.Text, await Revision(), 1);
        var foreign = await AttachmentService(f).UploadAsync(setup.Tenant, other.Owner.SessionId, setup.Owner, f.Ids.NewId(), await Revision(), 1,
            "Other Session secret.txt", "text/plain", new MemoryStream("Other Session bytes"u8.ToArray()));
        await f.RunService().SubmitMessageAsync(setup.Tenant, other.Owner.SessionId, setup.Owner, f.Ids.NewId(), "Other message", [foreign], await Revision(), 1);
        var receipt = await f.RunService().SubmitMessageAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Inspect", [id], await Revision(), 1);
        var runtime = (ITenantRuntimeStore)f.Iam; var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, receipt.RunId))!; var at = f.Clock.GetUtcNow();
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, setup.Session, run.AgentRunId, run.Claim!.Generation); var snapshot = (await runtime.LoadTenantRuntimeAsync(frame))!;
        var answer = new ConversationEntry(f.Ids.NewId(), 2, null, ConversationRole.Assistant, "Result", run.ResponseId, EntryStatus.Completed, SessionMode.Text, 0, 6, at,
            new ResponseEnvelope("Result", null, [new("file", ResponseBlockKind.AttachmentReference, "Invented private label", "Invented private fallback", id.ToString("D"), null, true),
                new("foreign", ResponseBlockKind.AttachmentReference, "Invented foreign label", "Invented foreign fallback", foreign.ToString("D"), null, true)], ResponseSpeechMode.Same),
            [new(foreign, "Invented foreign source", "text/plain")]);
        await runtime.SaveTenantRuntimeAsync(frame, snapshot with { Revision = snapshot.Revision + 1, Entries = [..snapshot.Entries, answer], LastEntrySequence = 2 }, snapshot.Revision);
        var service = new TenantConversationService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantConversationStore)f.Iam, f.Identity, f.Authorizer, attachmentAccess: AttachmentService(f));
        var ownerPage = await service.ReadAsync(setup.Tenant, setup.Session, setup.Owner); Assert.Equal("Private source.txt", Assert.Single(ownerPage.Items[0].Attachments!).DisplayName);
        var block = Assert.Single(ownerPage.Items[1].Blocks); Assert.Equal("File", block.FallbackText); Assert.Equal("", block.Text); Assert.Equal(id.ToString("D"), block.AttachmentId);
        Assert.DoesNotContain("Invented private", JsonSerializer.Serialize(ownerPage)); Assert.DoesNotContain("Invented foreign", JsonSerializer.Serialize(ownerPage));
        Assert.Null(ownerPage.Items[1].Attachments); Assert.DoesNotContain("Other Session secret.txt", JsonSerializer.Serialize(ownerPage));
        var human = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        var chat = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Chat and Session", ["agents.chat", "sessions.read"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, human.PrincipalId, chat.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        var hidden = await service.ReadAsync(setup.Tenant, setup.Session, human); Assert.All(hidden.Items, e => { Assert.Null(e.Attachments); Assert.Empty(e.Blocks); });
        Assert.DoesNotContain("Private source.txt", JsonSerializer.Serialize(hidden));
        var read = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Exact attachment reader", ["attachments.read"], await Revision());
        var assignment = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, human.PrincipalId, read.RoleId, TenantAttachmentRules.Scope(setup.Tenant, id), await Revision());
        Assert.Single((await service.ReadAsync(setup.Tenant, setup.Session, human)).Items[0].Attachments!);
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, assignment.AssignmentId, await Revision());
        Assert.Null((await service.ReadAsync(setup.Tenant, setup.Session, human)).Items[0].Attachments);
        await Assert.ThrowsAsync<AccessDeniedException>(() => AttachmentService(f).ReadAsync(setup.Tenant, other.Owner.SessionId, id, setup.Owner).AsTask());
    }
}
