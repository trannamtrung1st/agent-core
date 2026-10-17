using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory]
    [InlineData(false, "new")] [InlineData(true, "new")]
    [InlineData(false, "newer")] [InlineData(true, "newer")]
    [InlineData(false, "wrong-response")] [InlineData(true, "wrong-response")]
    [InlineData(false, "collision")] [InlineData(true, "collision")]
    [InlineData(false, "receipt-regression")] [InlineData(true, "receipt-regression")]
    [InlineData(false, "negative-receipt")] [InlineData(true, "negative-receipt")]
    public async Task Tenant_cancellation_atomically_closes_mailbox_response_before_checkpoint_without_overwriting_unrelated_rows_or_receipts(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Original request", await Revision(), 1);
        var runtime = (ITenantRuntimeStore)f.Iam; var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, receipt.RunId))!;
        var at = f.Clock.GetUtcNow();
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, setup.Session, run.AgentRunId, run.Claim!.Generation);
        var snapshot = (await runtime.LoadTenantRuntimeAsync(frame))!;
        var response = new ConversationEntry(f.Ids.NewId(), 2, null, ConversationRole.Assistant, "Hello", run.ResponseId, EntryStatus.Streaming, SessionMode.Text, 0,
            boundary is "newer" or "receipt-regression" ? 2 : 0, at);
        if (boundary is "newer" or "receipt-regression")
        {
            await runtime.SaveTenantRuntimeAsync(frame, snapshot with { Revision = snapshot.Revision + 1, Entries = [..snapshot.Entries, response],
                LastEntrySequence = response.Sequence }, snapshot.Revision);
            snapshot = (await runtime.LoadTenantRuntimeAsync(frame))!;
        }
        var evidence = boundary switch {
            "newer" => response with { Text = "Hello from the mailbox" },
            "receipt-regression" => response with { ReceivedTextEndExclusive = 0 },
            "negative-receipt" => response with { ReceivedTextEndExclusive = -1, HeardTextEndExclusive = -1 },
            "wrong-response" => response with { ResponseId = f.Ids.NewId() },
            "collision" => response with { Sequence = 1 },
            _ => response
        };
        var service = new TenantRunCancellationService(f.Iam, (ITenantRunStore)f.Iam, f.Identity, f.Authorizer, f.Ids, f.Clock);
        var securityRevision = await Revision(); var request = await service.PrepareAsync(setup.Tenant, run.AgentRunId, setup.Owner, securityRevision, run.Revision);
        if (boundary is "wrong-response" or "collision" or "receipt-regression" or "negative-receipt")
        {
            await Assert.ThrowsAsync<SecurityConflictException>(() => service.CommitAsync(request, response: evidence).AsTask());
            Assert.Equal(securityRevision, await Revision());
            Assert.Equal(AgentRunStatus.Running, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!.Status);
            Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 100), a => a.Action == "agent-runs.cancel");
            Assert.Equal(snapshot.Revision, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision);
            return;
        }
        var stopped = await service.CommitAsync(request, response: evidence); Assert.Equal(AgentRunStatus.Cancelled, stopped.Status);
        ConversationEntry retained;
        if (sqlite)
        {
            await using var db = await f.Contexts!.CreateDbContextAsync();
            retained = SqliteMemoryStore.ToEntry(await db.Entries.SingleAsync(e => e.EntryId == response.EntryId.ToString("D")));
        }
        else retained = ((InMemoryMemoryStore)f.Memory).FindEntry(setup.Session, response.EntryId)!;
        Assert.Equal(evidence.Text, retained.Text); Assert.Equal(evidence.ReceivedTextEndExclusive, retained.ReceivedTextEndExclusive);
        Assert.Equal(EntryStatus.Interrupted, retained.Status); Assert.Equal("userStop", retained.InterruptReason); Assert.NotNull(retained.CompletedAt);
        var final = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot;
        Assert.Equal(snapshot.Revision + 1, final.Revision); Assert.Equal(2, final.DurableLastEntrySequence);
        Assert.Equal(securityRevision + 1, await Revision());
        var read = await new TenantConversationService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantConversationStore)f.Iam, f.Identity, f.Authorizer)
            .ReadAsync(setup.Tenant, setup.Session, setup.Owner);
        Assert.Equal("Original request", read.Items[0].Text); Assert.Equal(evidence.Text[..evidence.ReceivedTextEndExclusive], read.Items[1].Text);
    }
}
