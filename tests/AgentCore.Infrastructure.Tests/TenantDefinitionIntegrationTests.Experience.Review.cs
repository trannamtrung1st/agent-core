using AgentCore.Application.Execution;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantDefinitionIntegrationTests
{
    private static TenantExperienceReviewService ExperienceReviewService(Fixture f, IIamStore? store = null)
    {
        store ??= f.Iam; var builtIns = new SessionNoBuiltinFallback();
        var resolver = new TenantRunConfigurationResolver((ITenantAgentStore)store, f.Definitions, builtIns,
            new(f.Resources, builtIns, new NoBuiltinKnowledge(), f.Content), (ITenantCredentialBindingStore)store);
        return new(store, f.Identity, (ITenantExperienceReviewStore)store, resolver, ModelCatalogFactory.Synthetic(), f.Ids, TimeProvider.System);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_manual_review_admits_one_human_root_graph_and_records_private_facts_through_normal_tools(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceOwner(f); var seed = await MemoryClaim(f, owner); await MemoryCommit(f, seed, await MemoryPlan(f, seed));
        var service = ExperienceReviewService(f); async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision;
        var first = await service.RequestAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, ExperienceSourceKind.Session, seed.Frame.SessionId, await Revision(), owner.Agent.Instance.Revision, TenantExperienceReviewRules.Fingerprint(await ((ITenantExperienceReviewStore)f.Iam).ObserveOwnTenantExperienceReviewAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, ExperienceSourceKind.Session, seed.Frame.SessionId)));
        Assert.True(first.Created); if (sqlite) service = ExperienceReviewService(f, new SqliteIamStore(f.Contexts!, f.Content));
        var again = await service.RequestAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, ExperienceSourceKind.Session, seed.Frame.SessionId, await Revision(), owner.Agent.Instance.Revision, TenantExperienceReviewRules.Fingerprint(await ((ITenantExperienceReviewStore)f.Iam).ObserveOwnTenantExperienceReviewAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, ExperienceSourceKind.Session, seed.Frame.SessionId)));
        Assert.False(again.Created); Assert.Equal(first.RunId, again.RunId); Assert.Equal(first.SessionId, again.SessionId);
        var runs = (ITenantRunStore)f.Iam; var run = (await runs.FindTenantRunAsync(owner.Tenant, first.RunId))!;
        Assert.Equal(ExecutionOrigin.UserMessage, run.Admission.ExecutionContext!.Origin); Assert.Equal(owner.Human.PrincipalId, run.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
        Assert.Equal(run.ActivationId, run.Admission.Activation.RootActivationId); Assert.Null(run.Admission.ExecutionContext.ParentRunId); Assert.Equal(AgentRunOutputContract.BackgroundOutcome, run.Admission.OutputContract);
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(owner.Tenant, first.SessionId))!;
        Assert.Equal(SessionOriginKind.ManualBackground, session.Snapshot.Origin.Kind); Assert.Equal(SessionSurface.BackgroundWork, session.Snapshot.Surfaces); Assert.Null(session.Snapshot.ProfileId);
        Assert.Equal(1, session.Snapshot.DurableLastEntrySequence); Assert.Equal(owner.Human.PrincipalId, session.Owner.CreatedByPrincipalId);
        var runtime = (ITenantRuntimeStore)f.Iam; run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, DateTimeOffset.UtcNow, f.Ids.NewId(), DateTimeOffset.UtcNow.AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation); Assert.Single((await runtime.LoadTenantRuntimeAsync(frame))!.Entries); var services = ExperienceServices(f);
        var inspected = await ((ITenantExperienceStore)f.Iam).ObserveTenantExperienceSourceAsync(new(owner.Tenant, run.AgentRunId), frame.Generation, ExperienceSourceKind.Session, seed.Frame.SessionId);
        await SaveExperienceSource(f, new(run, frame, session.Snapshot, session.Snapshot), inspected);
        var body = await services.Experience.ExecuteAsync(frame, new("record-manual-review", TenantExperienceToolContract.Record, ExperienceRecordArguments(inspected.Witness)), 6000); Assert.Contains("\"applied\":true", body);
        var fact = Assert.Single((await ((ITenantExperienceManagementStore)f.Iam).ReadOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new())).Items).Experience;
        Assert.Equal(owner.Human.PrincipalId, fact.UserPrincipalId); Assert.Equal(owner.Human.AccountId, fact.AccountId); Assert.Equal(first.RunId, fact.RootRunId);
        Assert.Single(await f.Iam.ReadAuditAsync(owner.Tenant, 200), a => a.Action == TenantExperienceReviewRules.AuditAction);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_manual_review_rechecks_original_login_source_and_security_before_atomic_graph_commit(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceOwner(f); var seed = await MemoryClaim(f, owner); await MemoryCommit(f, seed, await MemoryPlan(f, seed));
        var store = (ITenantExperienceReviewStore)f.Iam; var selected = await store.ObserveOwnTenantExperienceReviewAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, ExperienceSourceKind.Session, seed.Frame.SessionId);
        var builtIns = new SessionNoBuiltinFallback(); var resolver = new TenantRunConfigurationResolver((ITenantAgentStore)f.Iam, f.Definitions, builtIns, new(f.Resources, builtIns, new NoBuiltinKnowledge(), f.Content), (ITenantCredentialBindingStore)f.Iam);
        var (agent, resolved) = await resolver.ResolveAsync(owner.Tenant, owner.Agent.Instance.InstanceId, default); var at = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var plan = TenantExperienceReviewRules.Build(selected, agent, resolved, seed.Run.PinnedModel, at);
        var audit = new AuthorizationAudit(f.Ids.NewId(), owner.Tenant, owner.Human.PrincipalId, TenantExperienceReviewRules.AuditAction, plan.Run.AgentRunId.ToString("D"), "allowed", "experience_review_admitted", at);
        await ChangeHistory(f, owner, seed.Frame.SessionId); Assert.Null(await store.TryAdmitOwnTenantExperienceReviewAsync(plan, owner.Human, audit));
        Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, plan.Run.AgentRunId)); Assert.Null(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(owner.Tenant, plan.Session.SessionId));
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(owner.Tenant, 200), a => a.Action == TenantExperienceReviewRules.AuditAction);
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.ObserveOwnTenantExperienceReviewAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human with { AuthenticationSessionId = f.Ids.NewId() }, ExperienceSourceKind.Session, seed.Frame.SessionId).AsTask());
    }
    [Theory] [InlineData(false, "source")] [InlineData(true, "source")] [InlineData(false, "other")] [InlineData(true, "other")]
    [InlineData(false, "optout")] [InlineData(true, "optout")] [InlineData(false, "read")] [InlineData(true, "read")]
    public async Task Tenant_experience_manual_review_source_pin_rejects_changed_or_different_work_and_current_opt_out_or_read_loss(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceOwner(f); var seed = await MemoryClaim(f, owner); await MemoryCommit(f, seed, await MemoryPlan(f, seed));
        var other = await MemoryClaim(f, owner); await MemoryCommit(f, other, await MemoryPlan(f, other));
        var service = ExperienceReviewService(f); var selected = await service.ObserveAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, ExperienceSourceKind.Session, seed.Frame.SessionId);
        var receipt = await service.RequestAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, ExperienceSourceKind.Session, seed.Frame.SessionId, selected.SecurityRevision, selected.InstanceRevision, TenantExperienceReviewRules.Fingerprint(selected));
        var runtime = (ITenantRuntimeStore)f.Iam; var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, receipt.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, DateTimeOffset.UtcNow, f.Ids.NewId(), DateTimeOffset.UtcNow.AddMinutes(5)), f.Ids.NewId());
        if (boundary == "source") await ChangeHistory(f, owner, seed.Frame.SessionId);
        if (boundary == "optout") owner = await ExperienceSettings(f, owner, false);
        if (boundary == "read") await MemoryPermissions(f, owner, "agents.read", "agents.chat", "memory.read", "memory.manage");
        await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantExperienceStore)f.Iam).ObserveTenantExperienceSourceAsync(new(owner.Tenant, run.AgentRunId), run.Claim!.Generation,
            ExperienceSourceKind.Session, boundary == "other" ? other.Frame.SessionId : seed.Frame.SessionId).AsTask());
        Assert.Empty((await ((ITenantExperienceManagementStore)f.Iam).ReadOwnTenantExperienceAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new())).Items);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_manual_review_does_not_treat_unreceived_or_nonterminal_work_as_completed_evidence(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceOwner(f); var running = await MemoryClaim(f, owner); var store = (ITenantExperienceReviewStore)f.Iam;
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.ObserveOwnTenantExperienceReviewAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, ExperienceSourceKind.Session, running.Frame.SessionId).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.ObserveOwnTenantExperienceReviewAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, ExperienceSourceKind.AgentRun, running.Frame.RunId).AsTask());
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(owner.Tenant, 200), i => i.Action == TenantExperienceReviewRules.AuditAction);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_manual_review_cannot_adopt_another_Users_private_Run_evidence_despite_raw_read_and_creation_rights(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await ExperienceSettings(f, await MemoryOwner(f, operations: true), true);
        var learned = await MemoryClaim(f, owner); await MemoryCommit(f, learned, await MemoryPlan(f, learned, Learned("OWNER_PRIVATE_FACT")));
        var source = await MemoryClaim(f, owner); var observed = await ((ITenantLearnedMemoryStore)f.Iam).ReadTenantRunMemoryAsync(source.Frame); Assert.NotEmpty(observed.Sources);
        var runtime = (ITenantRuntimeStore)f.Iam;
        var saved = await runtime.ApplyTenantRuntimeCommandAsync(source.Run.Owner, source.Run.AgentRunId, new AgentRunCommand.Checkpoint(source.Run.Revision, DateTimeOffset.UtcNow, source.Frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, "{}", ToolCallId: "actual-observation", Name: ToolCatalog.AgentStateInspect)], executionState: new(TenantMemorySources: observed.Sources)), 1, 0, 180000, 0), null), f.Ids.NewId());
        var completed = await MemoryCommit(f, source, await MemoryPlan(f, source)); Assert.True(TenantExperienceRules.HasPrivateEvidence(completed));
        var account = await f.Identity.RegisterAsync("experience-review-peer@example.test", "Peer", "strong-experience-peer-password");
        var role = await f.Access.SaveRoleAsync(owner.Human.PrincipalId, owner.Tenant, null, "Peer independent review rights", ["agents.read", "agents.chat", "sessions.create", "sessions.read", "agent-runs.create", "agent-runs.read", "memory.read", "memory.manage"], (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var invite = await f.Access.InviteAsync(owner.Human.PrincipalId, owner.Tenant, account.LoginKey, role.RoleId, (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, owner.Tenant, invite.Invitation.InvitationId, invite.Token);
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-experience-peer-password"))!.Session; var peer = new HumanSessionReference(account.AccountId, login.SessionId, user.PrincipalId);
        var store = (ITenantExperienceReviewStore)f.Iam;
        Assert.Equal(owner.Human.PrincipalId, (await store.ObserveOwnTenantExperienceReviewAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, ExperienceSourceKind.AgentRun, completed.AgentRunId)).Witness.PrivateUserPrincipalId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.ObserveOwnTenantExperienceReviewAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, ExperienceSourceKind.AgentRun, completed.AgentRunId).AsTask());
    }

}
