using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory]
    [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "bounds")] [InlineData(true, "bounds")]
    [InlineData(false, "entry")] [InlineData(true, "entry")]
    [InlineData(false, "owner")] [InlineData(true, "owner")]
    public async Task Tenant_receipt_writer_rejects_stale_human_or_forged_delivery_evidence_without_committing_credit(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var submitted = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Request", await Revision(), 1);
        var runtime = (ITenantRuntimeStore)f.Iam; var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, submitted.RunId))!;
        var at = f.Clock.GetUtcNow();
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, setup.Session, run.AgentRunId, run.Claim!.Generation);
        var snapshot = (await runtime.LoadTenantRuntimeAsync(frame))!;
        var response = new ConversationEntry(f.Ids.NewId(), 2, null, ConversationRole.Assistant, "Hello", run.ResponseId, EntryStatus.Streaming, SessionMode.Text, 0, 0, at);
        await runtime.SaveTenantRuntimeAsync(frame, snapshot with { Revision = snapshot.Revision + 1, Entries = [.. snapshot.Entries, response], LastEntrySequence = 2 }, snapshot.Revision);
        snapshot = (await runtime.LoadTenantRuntimeAsync(frame))!;
        var recipient = await new TenantConversationService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantConversationStore)f.Iam, f.Identity, f.Authorizer)
            .AuthorizeRecipientAsync(setup.Tenant, setup.Session, setup.Owner);
        var proof = new TenantDeliveryReceipt(recipient.Owner, setup.Owner, recipient.SecurityRevision, response.EntryId, response.Sequence, run.ResponseId!.Value, 5, 5);
        if (boundary == "revoke") await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Unrelated authority change", ["sessions.read"], await Revision());
        if (boundary == "logout") await f.Identity.SignOutAsync(setup.Owner.AuthenticationSessionId);
        if (boundary == "bounds") proof = proof with { PublishedEnd = 4 };
        if (boundary == "entry") proof = proof with { EntrySequence = 1 };
        if (boundary == "owner") proof = proof with { Owner = proof.Owner with { AgentInstanceId = f.Ids.NewId() } };
        var store = (ITenantDeliveryReceiptStore)f.Iam;
        var proposed = snapshot with { Revision = snapshot.Revision + 1, Entries = snapshot.Entries.Select(e => e.EntryId == response.EntryId ? e with { ReceivedTextEndExclusive = 5 } : e).ToArray() };
        var error = await Record.ExceptionAsync(() => store.SaveTenantRuntimeReceiptAsync(frame, proposed, snapshot.Revision, proof).AsTask());
        Assert.True(error is AccessDeniedException or SecurityConflictException, error?.ToString());
        error = await Record.ExceptionAsync(() => store.SaveTenantQuiescentReceiptAsync(proof).AsTask());
        Assert.True(error is AccessDeniedException or SecurityConflictException, error?.ToString());
        var saved = (await runtime.LoadTenantRuntimeAsync(frame))!;
        Assert.Equal(snapshot.Revision, saved.Revision); Assert.Equal(0, saved.Entries.Single(e => e.EntryId == response.EntryId).ReceivedTextEndExclusive);
        Assert.Equal(AgentRunStatus.Running, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!.Status);
    }
}
