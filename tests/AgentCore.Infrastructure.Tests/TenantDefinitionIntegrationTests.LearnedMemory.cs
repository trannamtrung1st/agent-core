using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Providers;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    private sealed record MemorySetup(Guid Tenant, HumanSessionReference Human, TenantAgent Agent, Guid Role, Guid Assignment,
        Guid Definition, TenantSessionService Sessions, TenantRunService Runs);
    private sealed record MemoryTurn(AgentRun Run, TenantRuntimeFrame Frame, SessionSnapshot Before, SessionSnapshot Proposed);
    private static MemoryProposal Learned(string body = "Use typed durable evidence", MemoryScopeHint scope = MemoryScopeHint.User,
        MemoryKind kind = MemoryKind.Preference, MemoryProposalOperation operation = MemoryProposalOperation.Upsert) => new(operation, kind, "Evidence workflow", body, scope, MemoryProposalSource.UserExplicit);
    private static async Task<MemorySetup> MemoryOwner(Fixture f, bool read = true, bool manage = true, bool operations = false)
    {
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new HumanSessionReference(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(human.PrincipalId, owner.TenantId, "Owned learned-memory policy", await Revision());
        draft = await f.Service.UpdateAsync(human.PrincipalId, owner.TenantId, draft.DraftId,
            draft.Candidate with { MemoryPolicy = new(true, true, true, true, true) }, await Revision(), draft.Revision);
        var publication = await f.Service.PublishAsync(human.PrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision);
        var builtIns = new SessionNoBuiltinFallback(); var agents = new TenantAgentService(f.Iam, (ITenantAgentStore)f.Iam, builtIns, f.Authorizer, f.Ids, TimeProvider.System, ownedDefinitions: f.Definitions);
        var agent = await agents.CreateFromPublicationAsync(human.PrincipalId, owner.TenantId, Guid.Parse(publication.DefinitionId), 1, await Revision());
        var permissions = new List<string> { "sessions.read", "agents.chat" }; if (operations) permissions.Add("agents.read"); if (read) permissions.Add("memory.read"); if (manage) permissions.Add("memory.manage");
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Independent learned-memory rights", permissions, await Revision());
        var assignment = await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(owner.TenantId), await Revision());
        var sessions = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, f.Definitions,
            builtIns, f.Identity, f.Authorizer, ModelCatalogFactory.Synthetic(), new(), f.Ids, TimeProvider.System);
        var runs = new TenantRunService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam,
            new((ITenantAgentStore)f.Iam, f.Definitions, builtIns, new(f.Resources, builtIns, new NoBuiltinKnowledge(), f.Content), (ITenantCredentialBindingStore)f.Iam),
            f.Identity, f.Authorizer, ModelCatalogFactory.Synthetic(), f.Ids, TimeProvider.System);
        return new(owner.TenantId, human, agent, role.RoleId, assignment.AssignmentId, Guid.Parse(publication.DefinitionId), sessions, runs);
    }
    private static async Task<MemoryTurn> MemoryClaim(Fixture f, MemorySetup owner, Guid? existing = null, HumanSessionReference? requester = null, TenantAgent? otherAgent = null)
    {
        var human = requester ?? owner.Human; var actor = new TenantSessionInitiator(human.AccountId, human.AuthenticationSessionId, human.PrincipalId); var agent = otherAgent ?? owner.Agent;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision;
        var session = existing ?? (await owner.Sessions.CreateAsync(owner.Tenant, agent.Instance.InstanceId, actor, SessionMode.Text, await Revision(), agent.Instance.Revision)).Owner.SessionId;
        var before = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(owner.Tenant, session))!;
        var accepted = await owner.Runs.SubmitTextAsync(owner.Tenant, session, actor, f.Ids.NewId(), "Use durable evidence for future work", await Revision(), before.Snapshot.Revision);
        var store = (ITenantRuntimeStore)f.Iam; var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, accepted.RunId))!;
        run = await store.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, DateTimeOffset.UtcNow, f.Ids.NewId(), DateTimeOffset.UtcNow.AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, session, run.AgentRunId, run.Claim!.Generation); var snapshot = (await store.LoadTenantRuntimeAsync(frame))!;
        var now = DateTimeOffset.UtcNow; var entry = new ConversationEntry(f.Ids.NewId(), snapshot.DurableLastEntrySequence + 1, null, ConversationRole.Assistant,
            "Evidence response completed.", run.ResponseId, EntryStatus.Completed, SessionMode.Text, 0, 28, now, Envelope: new("Evidence response completed.", null, [], ResponseSpeechMode.None));
        return new(run, frame, snapshot, snapshot with { Revision = snapshot.Revision + 1, LastEntrySequence = entry.Sequence, Entries = [..snapshot.Entries, entry] });
    }
    private static ValueTask<TenantMemoryPlan> MemoryPlan(Fixture f, MemoryTurn turn, params MemoryProposal[] proposals) => ((ITenantLearnedMemoryStore)f.Iam)
        .PrepareTenantLearningAsync(turn.Frame, turn.Proposed, proposals, Enumerable.Range(0, TenantMemoryRules.CandidateCount).Select(_ => f.Ids.NewId()).ToArray(),
            f.Ids.NewId(), DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    private static async Task<AgentRun> MemoryCommit(Fixture f, MemoryTurn turn, TenantMemoryPlan plan, long? expected = null, TenantRuntimeFrame? frame = null)
    {
        var snapshot = turn.Proposed with { Entries = turn.Proposed.Entries.Select(e => e.ResponseId == turn.Run.ResponseId && e.Role == ConversationRole.Assistant
            ? e with { Envelope = e.Envelope! with { MemoryReceipts = plan.Receipts } } : e).ToArray() };
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(turn.Run.Owner.TenantId!.Value, turn.Run.AgentRunId))!;
        return await ((ITenantRuntimeStore)f.Iam).CommitTenantOutcomeAsync(frame ?? turn.Frame, snapshot, expected ?? turn.Before.Revision,
            new(run.Revision, DateTimeOffset.UtcNow, turn.Frame.Generation, "Evidence response completed.", AgentRunOutcomeKind.Response,
                snapshot.Entries.Single(e => e.Role == ConversationRole.Assistant && e.ResponseId == run.ResponseId).EntryId, plan), null, f.Ids.NewId());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_learned_memory_commits_actual_owned_scopes_and_recalls_without_global_Profile_or_Definition_fallback(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f); var first = await MemoryClaim(f, owner);
        var plan = await MemoryPlan(f, first, Learned()); Assert.Equal(3, plan.Changes.Count); Assert.Equal(3, Assert.Single(plan.Receipts).Scopes!.Count);
        Assert.All(plan.Changes, c => { Assert.Equal(owner.Human.PrincipalId, c.Value.UserPrincipalId); Assert.Equal(owner.Human.AccountId, c.Value.AccountId); });
        Assert.Equal(AgentRunStatus.Completed, (await MemoryCommit(f, first, plan)).Status);
        var same = await MemoryClaim(f, owner, first.Frame.SessionId); var reader = (ITenantLearnedMemoryStore)f.Iam;
        var local = await reader.ReadTenantRunMemoryAsync(same.Frame); Assert.Equal(MemoryScope.Session, Assert.Single(local.Items).Scope); await reader.RequireTenantMemoryProjectionAsync(same.Frame, local);
        var separate = await MemoryClaim(f, owner); var longTerm = await reader.ReadTenantRunMemoryAsync(separate.Frame); Assert.Equal(MemoryScope.IdentityUser, Assert.Single(longTerm.Items).Scope);
        var agents = new TenantAgentService(f.Iam, (ITenantAgentStore)f.Iam, new SessionNoBuiltinFallback(), f.Authorizer, f.Ids, TimeProvider.System, ownedDefinitions: f.Definitions);
        var secondAgent = await agents.CreateFromPublicationAsync(owner.Human.PrincipalId, owner.Tenant, owner.Definition, 1, (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(owner.Human.PrincipalId, owner.Tenant, secondAgent.PrincipalId, owner.Role, AccessScope.ForTenant(owner.Tenant), (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var other = await MemoryClaim(f, owner, otherAgent: secondAgent); Assert.Equal(MemoryScope.User, Assert.Single((await reader.ReadTenantRunMemoryAsync(other.Frame)).Items).Scope);
        if (sqlite)
        {
            reader = new SqliteIamStore(f.Contexts!, f.Content); Assert.Equal(longTerm.Items.Single().MemoryId, Assert.Single((await reader.ReadTenantRunMemoryAsync(separate.Frame)).Items).MemoryId);
            await using var db = f.Contexts!.CreateDbContext(); Assert.Equal(3, await db.Set<TenantLearnedMemoryRecord>().CountAsync()); Assert.False(db.Database.HasPendingModelChanges());
            Assert.Empty(await db.StructuredMemories.ToArrayAsync()); Assert.Empty(await db.Profiles.ToArrayAsync());
        }
        var audits = (await f.Iam.ReadAuditAsync(owner.Tenant, 200)).Where(a => a.Action == "memory.learn").ToArray(); Assert.Single(audits);
        Assert.DoesNotContain("Use typed durable evidence", JsonSerializer.Serialize(audits));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_learned_memory_failed_response_CAS_commits_no_learning_receipt_audit_or_partial_scopes(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f); var turn = await MemoryClaim(f, owner); var plan = await MemoryPlan(f, turn, Learned());
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => MemoryCommit(f, turn, plan, turn.Before.Revision + 1));
        Assert.Empty((await ((ITenantLearnedMemoryStore)f.Iam).ReadTenantRunMemoryAsync(turn.Frame)).Items);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(owner.Tenant, 200), a => a.Action == "memory.learn");
        Assert.Equal(AgentRunStatus.Running, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, turn.Run.AgentRunId))!.Status);
        Assert.Equal(AgentRunStatus.Completed, (await MemoryCommit(f, turn, plan)).Status);
        await Assert.ThrowsAsync<AccessDeniedException>(() => MemoryCommit(f, turn, plan));
        Assert.Single(await f.Iam.ReadAuditAsync(owner.Tenant, 200), a => a.Action == "memory.learn");
    }
    [Theory] [InlineData(false, "grant")] [InlineData(true, "grant")] [InlineData(false, "review")] [InlineData(true, "review")]
    [InlineData(false, "generation")] [InlineData(true, "generation")] [InlineData(false, "owner")] [InlineData(true, "owner")]
    [InlineData(false, "receipt")] [InlineData(true, "receipt")]
    public async Task Tenant_learned_memory_final_transaction_rechecks_raw_rights_policy_frame_and_exact_owner_receipts(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f); var turn = await MemoryClaim(f, owner); var plan = await MemoryPlan(f, turn, Learned());
        if (boundary == "grant") await f.Access.RevokeAssignmentAsync(owner.Human.PrincipalId, owner.Tenant, owner.Assignment, (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        if (boundary == "review") await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, TimeProvider.System)
            .SaveAsync(owner.Tenant, new(owner.Human.AccountId, owner.Human.AuthenticationSessionId, owner.Human.PrincipalId), "memory.manage", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision, 0);
        if (boundary == "owner") plan = plan with { Changes = plan.Changes.Select(c => c with { Value = c.Value with { UserPrincipalId = owner.Agent.PrincipalId } }).ToArray() };
        if (boundary == "receipt") plan = plan with { Receipts = plan.Receipts.Select(r => r with { Owner = r.Owner! with { UserPrincipalId = f.Ids.NewId() } }).ToArray() };
        await Assert.ThrowsAsync<AccessDeniedException>(() => MemoryCommit(f, turn, plan, frame: boundary == "generation" ? turn.Frame with { Generation = f.Ids.NewId() } : null));
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(owner.Tenant, 200), a => a.Action == "memory.learn");
        if (sqlite) { await using var db = f.Contexts!.CreateDbContext(); Assert.Empty(await db.Set<TenantLearnedMemoryRecord>().ToArrayAsync()); }
    }
    [Theory] [InlineData(false, true)] [InlineData(true, true)] [InlineData(false, false)] [InlineData(true, false)]
    public async Task Tenant_learned_memory_read_and_manage_are_independent_and_passive_learning_never_creates_consent(bool sqlite, bool read)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f, read: read, manage: !read); var turn = await MemoryClaim(f, owner); var plan = await MemoryPlan(f, turn, Learned());
        Assert.Equal(read ? "unavailable" : "stored", Assert.Single(plan.Receipts).Outcome); await MemoryCommit(f, turn, plan);
        var next = await MemoryClaim(f, owner); var view = await ((ITenantLearnedMemoryStore)f.Iam).ReadTenantRunMemoryAsync(next.Frame); Assert.Empty(view.Items);
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(owner.Tenant, null, null, new(0, 100))).Items);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_learned_memory_updates_and_resolves_owned_facts_atomically_without_erasing_provenance(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f); var turn = await MemoryClaim(f, owner);
        var first = await MemoryPlan(f, turn, Learned(kind: MemoryKind.OpenLoop)); await MemoryCommit(f, turn, first);
        var next = await MemoryClaim(f, owner, turn.Frame.SessionId); var updated = await MemoryPlan(f, next, Learned("Verified research is retained", kind: MemoryKind.OpenLoop));
        Assert.Equal(6, updated.Changes.Count); await MemoryCommit(f, next, updated);
        var close = await MemoryClaim(f, owner, turn.Frame.SessionId); var resolved = await MemoryPlan(f, close, Learned("", kind: MemoryKind.OpenLoop, operation: MemoryProposalOperation.Resolve));
        Assert.Equal("resolved", Assert.Single(resolved.Receipts).Outcome); Assert.Equal(3, resolved.Changes.Count); await MemoryCommit(f, close, resolved);
        var after = await MemoryClaim(f, owner, turn.Frame.SessionId); Assert.Empty((await ((ITenantLearnedMemoryStore)f.Iam).ReadTenantRunMemoryAsync(after.Frame)).Items);
        var again = await MemoryPlan(f, after, Learned("", kind: MemoryKind.OpenLoop, operation: MemoryProposalOperation.Resolve)); Assert.Equal("alreadyResolved", Assert.Single(again.Receipts).Outcome);
        if (sqlite) { await using var db = f.Contexts!.CreateDbContext(); var rows = await db.Set<TenantLearnedMemoryRecord>().ToArrayAsync(); Assert.Equal(6, rows.Length);
            Assert.Equal(3, rows.Count(i => i.Status == 1)); Assert.Equal(3, rows.Count(i => i.Status == 3)); Assert.All(rows, r => Assert.NotEmpty(r.Content)); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_learned_memory_same_conversation_uses_actual_requester_and_never_another_Users_owned_facts(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f); var original = await MemoryClaim(f, owner);
        await MemoryCommit(f, original, await MemoryPlan(f, original, Learned("OWNER_PRIVATE_FACT")));
        var account = await f.Identity.RegisterAsync("memory-peer@example.test", "Peer", "strong-memory-peer-password");
        var role = await f.Access.SaveRoleAsync(owner.Human.PrincipalId, owner.Tenant, null, "Peer memory rights", ["agents.chat", "sessions.create", "sessions.read", "memory.read", "memory.manage"], (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var invite = await f.Access.InviteAsync(owner.Human.PrincipalId, owner.Tenant, account.LoginKey, role.RoleId, (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, owner.Tenant, invite.Invitation.InvitationId, invite.Token);
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-memory-peer-password"))!.Session;
        var peer = new HumanSessionReference(account.AccountId, login.SessionId, user.PrincipalId);
        var next = await MemoryClaim(f, owner, original.Frame.SessionId, peer); var store = (ITenantLearnedMemoryStore)f.Iam;
        Assert.Empty((await store.ReadTenantRunMemoryAsync(next.Frame)).Items);
        var peerPlan = await MemoryPlan(f, next, Learned("PEER_PRIVATE_FACT")); await MemoryCommit(f, next, peerPlan);
        var ownerAgain = await MemoryClaim(f, owner, original.Frame.SessionId); var observed = await store.ReadTenantRunMemoryAsync(ownerAgain.Frame);
        Assert.Equal("OWNER_PRIVATE_FACT", Assert.Single(observed.Items).Content); Assert.Equal(owner.Human.PrincipalId, observed.UserPrincipalId);
        var peerAgain = await MemoryClaim(f, owner, requester: peer); Assert.Equal("PEER_PRIVATE_FACT", Assert.Single((await store.ReadTenantRunMemoryAsync(peerAgain.Frame)).Items).Content);
        var peerSource = Assert.Single((await store.ReadTenantRunMemoryAsync(peerAgain.Frame)).Sources);
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.RequireTenantMemorySourcesAsync(new(owner.Tenant, ownerAgain.Run.AgentRunId), [peerSource], ownerAgain.Frame.Generation).AsTask());
    }
    [Theory] [InlineData(false, "tenant")] [InlineData(true, "tenant")] [InlineData(false, "id")] [InlineData(true, "id")]
    [InlineData(false, "hash")] [InlineData(true, "hash")] [InlineData(false, "generation")] [InlineData(true, "generation")]
    [InlineData(false, "projection")] [InlineData(true, "projection")] [InlineData(false, "grant")] [InlineData(true, "grant")]
    public async Task Tenant_learned_memory_source_witness_and_projection_cannot_change_current_owner_value_or_raw_access(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f); var first = await MemoryClaim(f, owner); await MemoryCommit(f, first, await MemoryPlan(f, first, Learned()));
        var next = await MemoryClaim(f, owner); var store = (ITenantLearnedMemoryStore)f.Iam; var observation = await store.ReadTenantRunMemoryAsync(next.Frame);
        var source = Assert.Single(observation.Sources);
        if (boundary == "projection")
        {
            var forged = observation with { Items = observation.Items.Select(i => i with { Content = "FORGED_CAPTURED_VALUE" }).ToArray() };
            await Assert.ThrowsAsync<AccessDeniedException>(() => store.RequireTenantMemoryProjectionAsync(next.Frame, forged).AsTask()); return;
        }
        if (boundary == "grant") await f.Access.RevokeAssignmentAsync(owner.Human.PrincipalId, owner.Tenant, owner.Assignment, (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        source = boundary switch { "tenant" => source with { TenantId = f.Ids.NewId() }, "id" => source with { MemoryId = f.Ids.NewId() },
            "hash" => source with { ContentHash = new string('f', 64) }, _ => source };
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.RequireTenantMemorySourcesAsync(new(owner.Tenant, next.Run.AgentRunId), [source], boundary == "generation" ? f.Ids.NewId() : next.Frame.Generation).AsTask());
        if (boundary == "grant") Assert.Empty((await store.ReadTenantRunMemoryAsync(next.Frame)).Items);
        else await store.RequireTenantMemoryProjectionAsync(next.Frame, observation);
    }
}
