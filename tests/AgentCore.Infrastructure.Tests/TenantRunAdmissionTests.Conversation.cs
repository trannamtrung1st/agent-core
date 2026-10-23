using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private static TenantConversationService Conversation(Fixture f, ITenantConversationStore? store = null) =>
        new(f.Iam, (ITenantSessionStore)f.Iam, store ?? (ITenantConversationStore)f.Iam, f.Identity, f.Authorizer);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_conversation_pages_actual_owned_entries_without_claim_or_legacy_access_and_preserves_receipt_truth(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Private user input",
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        var runtime = (ITenantRuntimeStore)f.Iam; var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, receipt.RunId))!;
        var at = f.Clock.GetUtcNow();
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, setup.Session, run.AgentRunId, run.Claim!.Generation);
        var snapshot = (await runtime.LoadTenantRuntimeAsync(frame))!;
        var privateAttachment = f.Ids.NewId(); var privateArtifact = f.Ids.NewId();
        var envelope = new ResponseEnvelope("Completed generated answer", null, [
            new("text", ResponseBlockKind.Markdown, "Public markdown", "Public markdown", null, null, true),
            new("private-attachment", ResponseBlockKind.AttachmentReference, "Private attachment label", "Private attachment fallback", privateAttachment.ToString("D"), null, true),
            new("private-artifact", ResponseBlockKind.ArtifactReference, "Private artifact label", "Private artifact fallback", null, privateArtifact.ToString("D"), true)],
            ResponseSpeechMode.Same, [new("saved", "upsert", "fixture", "Private memory label", ["private"], "indicator", new(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner.PrincipalId))],
            [new("private.effect", "completed", "Private effect label")]);
        var entries = new[] {
            new ConversationEntry(f.Ids.NewId(), 2, f.Ids.NewId(), ConversationRole.Assistant, "Completed generated answer", run.ResponseId,
                EntryStatus.Completed, SessionMode.Text, 0, 0, at, envelope, [new(privateAttachment, "Private attachment name", "text/plain")], CompletedAt: at),
            new ConversationEntry(f.Ids.NewId(), 3, null, ConversationRole.Assistant, "visible private unreceived suffix", f.Ids.NewId(), EntryStatus.Interrupted,
                SessionMode.Voice, 2, 7, at, CompletedAt: at),
            new ConversationEntry(f.Ids.NewId(), 4, null, ConversationRole.Assistant, "stream private unreceived suffix", f.Ids.NewId(), EntryStatus.Streaming,
                SessionMode.Text, 0, 6, at)
        };
        await runtime.SaveTenantRuntimeAsync(frame, snapshot with { Entries = [..snapshot.Entries, ..entries], Revision = snapshot.Revision + 1,
            LastEntrySequence = 4, UpdatedAt = at }, snapshot.Revision);
        await f.RunService().CancelAsync(setup.Tenant, run.AgentRunId, setup.Owner, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, run.Revision);
        var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Read conversation", ["sessions.read", "agents.chat"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        var service = Conversation(f); var securityRevision = await Revision();
        var newest = await service.ReadAsync(setup.Tenant, setup.Session, member, limit: 2);
        Assert.Equal(new long[] {3, 4}, newest.Items.Select(e => e.Sequence)); Assert.True(newest.HasOlder); Assert.Equal(3, newest.NextBefore);
        Assert.Equal("visible", newest.Items[0].Text); Assert.Equal(2, newest.Items[0].HeardTextEndExclusive); Assert.Equal(7, newest.Items[0].ReceivedTextEndExclusive);
        Assert.Equal("stream", newest.Items[1].Text); Assert.Equal(6, newest.Items[1].ReceivedTextEndExclusive);
        var older = await service.ReadAsync(setup.Tenant, setup.Session, member, before: newest.NextBefore, limit: 2);
        Assert.Equal(new long[] {1, 2}, older.Items.Select(e => e.Sequence)); Assert.False(older.HasOlder);
        var answer = older.Items[1]; Assert.Equal("Completed generated answer", answer.Text); Assert.Equal(0, answer.ReceivedTextEndExclusive);
        Assert.Equal("text", Assert.Single(answer.Blocks).BlockId); Assert.Null(answer.Attachments); Assert.Null(answer.SourceEventId);
        Assert.Null(answer.MemoryReceipts); Assert.Null(answer.EffectReceipts);
        var ownerAnswer = (await service.ReadAsync(setup.Tenant, setup.Session, setup.Owner, before: 3)).Items.Last();
        Assert.NotNull(ownerAnswer.MemoryReceipts); Assert.Null(ownerAnswer.Attachments);
        Assert.Equal("text", Assert.Single(ownerAnswer.Blocks).BlockId); // Tenant-wide permission cannot invent resource ownership.
        var json = JsonSerializer.Serialize(older); Assert.DoesNotContain("Private attachment", json); Assert.DoesNotContain("Private artifact", json);
        Assert.DoesNotContain("Private memory", json); Assert.DoesNotContain("Private effect", json); Assert.DoesNotContain(privateAttachment.ToString(), json);
        var forward = await service.ReadAsync(setup.Tenant, setup.Session, member, after: 2, limit: 1);
        Assert.Equal(3, Assert.Single(forward.Items).Sequence); Assert.True(forward.HasMore); Assert.Equal(3, forward.NextAfter);
        var empty = await service.ReadAsync(setup.Tenant, setup.Session, member, after: 4);
        Assert.Empty(empty.Items); Assert.Equal(4, empty.NextAfter); Assert.False(empty.HasMore);
        Assert.Equal(securityRevision, await Revision()); Assert.Equal(newest.SessionRevision, empty.SessionRevision);
        Assert.Empty(await f.Memory.ReadHistoryAsync(setup.Session, 0, 100));
        if (sqlite)
        {
            var reopened = await Conversation(f, new SqliteIamStore(f.Contexts!)).ReadAsync(setup.Tenant, setup.Session, member, before: 3);
            Assert.Equal(older.Items.Select(e => e.Text), reopened.Items.Select(e => e.Text));
        }
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == setup.Owner.AccountId);
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, setup.Owner.AccountId, "Beta", "beta");
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(beta.Tenant.TenantId, setup.Session,
            setup.Owner with { PrincipalId = beta.Users.Single().PrincipalId }).AsTask());
    }

    private sealed class ConversationGate(ITenantConversationStore inner) : ITenantConversationStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<TenantConversationPage?> ReadTenantConversationAsync(Guid tenant, Guid session, long? after, long? before, int limit, CancellationToken ct = default)
        {
            var page = await inner.ReadTenantConversationAsync(tenant, session, after, before, limit, ct);
            Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return page;
        }
    }
    [Theory]
    [InlineData(false, "read")] [InlineData(true, "read")]
    [InlineData(false, "chat")] [InlineData(true, "chat")]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "account")] [InlineData(true, "account")]
    [InlineData(false, "delete")] [InlineData(true, "delete")]
    [InlineData(false, "compete")] [InlineData(true, "compete")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task Tenant_conversation_rechecks_actual_recipient_after_content_IO_without_leaking_a_stale_page(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Private input", await Revision(), 1);
        await f.RunService().CancelAsync(setup.Tenant, receipt.RunId, setup.Owner, await Revision(), 1);
        var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Read conversation", ["sessions.read", "agents.chat"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        var gate = new ConversationGate((ITenantConversationStore)f.Iam); using var stop = new CancellationTokenSource();
        var pending = Conversation(f, gate).ReadAsync(setup.Tenant, setup.Session, member, ct: stop.Token).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (boundary is "read" or "chat")
            await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, role.RoleId, role.Name, boundary == "read" ? ["agents.chat"] : ["sessions.read"], await Revision());
        if (boundary == "logout") await f.Identity.SignOutAsync(member.AuthenticationSessionId);
        if (boundary == "account")
        {
            var account = (await f.Iam.ReadAccountAsync(member.AccountId))!;
            Assert.True(await f.Iam.TryUpdateAccountAsync(account with { Status = IdentityStatus.Suspended, SecurityStamp = "suspended-reader" },
                account.SecurityStamp, new(f.Ids.NewId(), null, setup.Owner.PrincipalId, "system.accounts.manage", member.AccountId.ToString("D"), "allowed", "account_suspended", f.Clock.GetUtcNow())));
        }
        if (boundary == "delete") await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(setup.Session, 2, TenantSessionAction.Delete), await Revision());
        if (boundary == "compete") await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Newer input", await Revision(), 2);
        if (boundary == "cancel") stop.Cancel();
        gate.Release.TrySetResult();
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else if (boundary == "compete") await Assert.ThrowsAsync<SecurityConflictException>(() => pending);
        else await Assert.ThrowsAsync<AccessDeniedException>(() => pending);
        if (boundary is "read" or "chat")
            await Assert.ThrowsAsync<AccessDeniedException>(() => Conversation(f).ReadAsync(setup.Tenant, setup.Session, member).AsTask());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_conversation_requires_both_exact_Session_and_agent_scopes_without_granting_sibling_access(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Read exact conversation", ["sessions.read", "agents.chat"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, role.RoleId,
            TenantSessionRules.Scope(setup.Tenant, setup.Session), await Revision());
        Assert.NotNull(await f.Sessions.ReadAsync(member.PrincipalId, setup.Tenant, setup.Session));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Conversation(f).ReadAsync(setup.Tenant, setup.Session, member).AsTask());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, role.RoleId,
            AccessScope.ForResource(setup.Tenant, "agent", setup.Agent.Instance.InstanceId.ToString("D")), await Revision());
        Assert.Empty((await Conversation(f).ReadAsync(setup.Tenant, setup.Session, member)).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Conversation(f).ReadAsync(setup.Tenant, setup.Session,
            member with { PrincipalId = setup.Owner.PrincipalId }).AsTask());
        var sibling = await f.Sessions.CreateAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner, SessionMode.Text, await Revision(), 1);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Conversation(f).ReadAsync(setup.Tenant, sibling.Owner.SessionId, member).AsTask());
    }
}
