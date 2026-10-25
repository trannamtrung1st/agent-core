using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private static async Task<(AgentRun Run,TenantRuntimeFrame Frame,SessionSnapshot Snapshot)> ClaimBackground(Fixture f,AgentRun run)
    {
        var store=(ITenantRuntimeStore)f.Iam;
        run=await store.ApplyTenantRuntimeCommandAsync(run.Owner,run.AgentRunId,new AgentRunCommand.Claim(run.Revision,f.Clock.GetUtcNow(),f.Ids.NewId(),f.Clock.GetUtcNow().AddMinutes(5)),f.Ids.NewId());
        var frame=new TenantRuntimeFrame(run.Owner,run.SessionId,run.AgentRunId,run.Claim!.Generation);
        return(run,frame,(await store.LoadTenantRuntimeAsync(frame))!);
    }
    private static (SessionSnapshot Snapshot,AgentRunCommand.Complete Complete) BackgroundOutcome(Fixture f,AgentRun run,SessionSnapshot snapshot,bool quiet=false)
    {
        var entry=new ConversationEntry(f.Ids.NewId(),snapshot.DurableLastEntrySequence+1,run.ActivationId,ConversationRole.Assistant,"Owned result",run.ResponseId,EntryStatus.Completed,SessionMode.Text,0,12,f.Clock.GetUtcNow());
        var next=snapshot with{Revision=snapshot.Revision+1,LastEntrySequence=quiet?snapshot.DurableLastEntrySequence:entry.Sequence,Entries=quiet?snapshot.Entries:[..snapshot.Entries,entry]};
        return(next,new(run.Revision,f.Clock.GetUtcNow(),run.Claim!.Generation,quiet?"Quiet result":"Owned result",quiet?AgentRunOutcomeKind.NoAction:AgentRunOutcomeKind.Response,quiet?null:entry.EntryId));
    }
    private static async Task CompleteBackgroundParent(Fixture f,BackgroundSetup parent)
    {
        await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(parent.Parent.Owner,parent.Parent.AgentRunId,
            new AgentRunCommand.Complete(parent.Parent.Revision,f.Clock.GetUtcNow(),parent.Frame.Generation,"Parent handled",AgentRunOutcomeKind.NoAction,null),f.Ids.NewId());
    }
    private static async Task<AgentRun> PrepareBackgroundReport(Fixture f,TenantBackgroundCompletion source)
    {
        var configuration=(await f.RunConfiguration().ResolveInSpaceAsync(source.Child.Owner.TenantId!.Value,source.Child.AgentInstanceId,source.Parent!.Snapshot.ContextSelection!,default)).Configuration;
        return TenantBackgroundCompletionFactory.Create(source,configuration,source.Parent!.Snapshot.ModelSelection!,f.Ids.NewId(),f.Ids.NewId(),f.Ids.NewId(),f.Clock.GetUtcNow());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_background_surface_filters_before_paging_and_cannot_implicitly_become_a_chat(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var parent = await BackgroundParent(f); var child = await BackgroundChild(f, parent);
        await ((ITenantRuntimeStore)f.Iam).AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var sessions = (ITenantSessionStore)f.Iam;
        Assert.Equal(2, (await sessions.ListTenantSessionsAsync(parent.Setup.Tenant, null, 0, 100)).Total);
        var chats = await sessions.ListTenantSessionsAsync(parent.Setup.Tenant, null, 0, 1, surface: SessionSurface.ChatList);
        Assert.Equal(1, chats.Total); Assert.Equal(parent.Setup.Session, Assert.Single(chats.Items).Owner.SessionId);
        var nextPage = await sessions.ListTenantSessionsAsync(parent.Setup.Tenant, null, 1, 1, surface: SessionSurface.ChatList);
        Assert.Equal(1, nextPage.Total); Assert.Empty(nextPage.Items);
        var work = await sessions.ListTenantSessionsAsync(parent.Setup.Tenant, null, 0, 1, surface: SessionSurface.BackgroundWork);
        Assert.Equal(child.Session.SessionId, Assert.Single(work.Items).Owner.SessionId);
        Assert.Single((await f.Sessions.ListAsync(parent.Setup.Tenant, parent.Setup.Owner)).Items);
        var conversation = Conversation(f);
        Assert.Equal(child.Session.SessionId, (await conversation.ReadViewAsync(parent.Setup.Tenant, child.Session.SessionId, parent.Setup.Owner)).Owner.SessionId);
        Assert.NotEmpty((await conversation.ReadAsync(parent.Setup.Tenant, child.Session.SessionId, parent.Setup.Owner)).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => conversation.AuthorizeRecipientAsync(parent.Setup.Tenant, child.Session.SessionId, parent.Setup.Owner).AsTask());
        var revision = (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision;
        await Assert.ThrowsAsync<AgentCoreException>(() => f.RunService().SubmitTextAsync(parent.Setup.Tenant, child.Session.SessionId, parent.Setup.Owner,
            f.Ids.NewId(), "Implicit foreground", revision, child.Session.Revision).AsTask());
        Assert.Single(await ((ITenantRunStore)f.Iam).ListTenantRunIdsAsync(parent.Setup.Tenant, child.Session.SessionId));
        Assert.Equal(SessionSurface.BackgroundWork, (await sessions.FindTenantSessionAsync(parent.Setup.Tenant, child.Session.SessionId))!.Snapshot.Surfaces);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_background_runtime_commits_outcome_and_one_root_bound_report_with_no_legacy_delivery(bool sqlite)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true);var parent=await BackgroundParent(f);await WaitReadAuthority(f,parent);var child=await BackgroundChild(f,parent);var store=(ITenantRuntimeStore)f.Iam;
        var bound=new TenantAgentRunStore(store,(ITenantRunStore)f.Iam,f.Ids,parent.Frame);
        Assert.True((await bound.AdmitImmediateAsync(child.Session,child.Run,parent.Frame.Generation)).Created);
        Assert.Contains(await store.ListRunnableTenantRunsAsync(f.Clock.GetUtcNow(),100),r=>r.AgentRunId==child.Run.AgentRunId);
        var claimed=await ClaimBackground(f,child.Run);var result=BackgroundOutcome(f,claimed.Run,claimed.Snapshot);
        var saved=await new TenantAgentRunStore(store,(ITenantRunStore)f.Iam,f.Ids,claimed.Frame).CommitOutcomeAsync(result.Snapshot,claimed.Snapshot.Revision,claimed.Run.Owner,claimed.Run.AgentRunId,result.Complete,null);
        Assert.Equal(AgentRunStatus.Completed,saved.Status);Assert.Equal("Owned result",saved.Result!.Text);
        Assert.Empty(await store.ListTenantBackgroundCompletionsAsync(100));
        var source=new TenantBackgroundCompletion(saved,(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(parent.Setup.Tenant,child.Session.SessionId))!,
            await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(parent.Setup.Tenant,parent.Setup.Session));var report=await PrepareBackgroundReport(f,source);
        await Assert.ThrowsAsync<SecurityConflictException>(()=>store.AdmitTenantBackgroundCompletionAsync(report,source.Parent!.Snapshot.Revision,f.Ids.NewId()).AsTask());
        await CompleteBackgroundParent(f,parent);source=Assert.Single(await store.ListTenantBackgroundCompletionsAsync(100));report=await PrepareBackgroundReport(f,source);
        var admitted=await store.AdmitTenantBackgroundCompletionAsync(report,source.Parent!.Snapshot.Revision,f.Ids.NewId());Assert.True(admitted.Created);
        var retry=await PrepareBackgroundReport(f,source);Assert.False((await store.AdmitTenantBackgroundCompletionAsync(retry,source.Parent.Snapshot.Revision,f.Ids.NewId())).Created);
        var reporting=await ClaimBackground(f,report);Assert.Equal(source.Parent.Snapshot.DurableLastEntrySequence,TenantBackgroundCompletionFactory.InputBoundary(report));
        Assert.Equal(parent.Parent.Admission.ExecutionContext!.RootActivationId,report.Admission.ExecutionContext!.RootActivationId);
        Assert.Equal(parent.Parent.AgentRunId,(await TenantExecutionLineage.RootAsync(report,(ITenantRunStore)f.Iam,default)).AgentRunId);
        var notification=BackgroundOutcome(f,reporting.Run,reporting.Snapshot);await store.CommitTenantOutcomeAsync(reporting.Frame,notification.Snapshot,reporting.Snapshot.Revision,notification.Complete,null,f.Ids.NewId());
        Assert.Empty(await store.ListTenantBackgroundCompletionsAsync(100));
        Assert.Single(await f.Iam.ReadAuditAsync(parent.Setup.Tenant,200),a=>a.Action=="agent-runs.background.report");
        IAgentRunStore legacy=sqlite?new SqliteAgentRunStore(f.Contexts!,(SqliteMemoryStore)f.Memory,new Diagnostics()):new InMemoryAgentRunStore((InMemoryMemoryStore)f.Memory,new Diagnostics());
        Assert.Empty(await legacy.ListDeliveryCandidatesAsync(100,f.Clock.GetUtcNow()));
        Assert.Empty(await legacy.ListUnreportedCompletionsAsync(100));
        await Assert.ThrowsAsync<AccessDeniedException>(()=>legacy.GetCompletionInboxAsync(child.Run.Owner,parent.Setup.Session,child.Run.AgentRunId,f.Clock.GetUtcNow()).AsTask());
        if(sqlite)
        {
            await using var db=f.Contexts!.CreateDbContext();var receipt=await db.BackgroundCompletionReceipts.SingleAsync(r=>r.ChildAgentRunId==child.Run.AgentRunId.ToString("D"));
            Assert.Null(receipt.ProfileId);Assert.Equal((int)CompletionInboxStatus.Delivered,receipt.Status);Assert.False(db.Database.HasPendingModelChanges());
            var reopened=new SqliteIamStore(f.Contexts,builtIns:f.BuiltIns,clock:f.Clock);Assert.Equal(AgentRunStatus.Completed,(await reopened.FindTenantRunAsync(parent.Setup.Tenant,report.AgentRunId))!.Status);
            Assert.Empty(await reopened.ListTenantBackgroundCompletionsAsync(100));
            await Assert.ThrowsAsync<SqliteException>(()=>db.Database.ExecuteSqlRawAsync("UPDATE BackgroundCompletionReceipts SET ProfileId='forged'"));
            await Assert.ThrowsAnyAsync<Exception>(()=>db.GetService<IMigrator>().MigrateAsync("20261020090000_TenantAgentDecommission"));
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_background_quiet_outcome_is_saved_without_pending_report_or_old_generation_writes(bool sqlite)
    {
        var clock=new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T02:00:00Z"));await using var f=await Fixture.Create(sqlite,clock,builtInAuthority:true);
        var parent=await BackgroundParent(f);var child=await BackgroundChild(f,parent);var store=(ITenantRuntimeStore)f.Iam;await store.AdmitTenantImmediateAsync(parent.Frame,child.Session,child.Run,f.Ids.NewId());
        var claimed=await ClaimBackground(f,child.Run);var result=BackgroundOutcome(f,claimed.Run,claimed.Snapshot,true);
        await Assert.ThrowsAsync<AccessDeniedException>(()=>store.CommitTenantOutcomeAsync(claimed.Frame with{Generation=f.Ids.NewId()},result.Snapshot,claimed.Snapshot.Revision,result.Complete,null,f.Ids.NewId()).AsTask());
        Assert.Equal(AgentRunStatus.Running,(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(parent.Setup.Tenant,child.Run.AgentRunId))!.Status);
        var done=await store.CommitTenantOutcomeAsync(claimed.Frame,result.Snapshot,claimed.Snapshot.Revision,result.Complete,null,f.Ids.NewId());
        Assert.Equal(AgentRunOutcomeKind.NoAction,done.Result!.OutcomeKind);Assert.Empty(await store.ListTenantBackgroundCompletionsAsync(100));
        await Assert.ThrowsAsync<AccessDeniedException>(()=>store.CommitTenantOutcomeAsync(claimed.Frame,result.Snapshot,claimed.Snapshot.Revision,result.Complete,null,f.Ids.NewId()).AsTask());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_background_claim_recovery_fences_the_old_writer_and_reuses_one_child_identity(bool sqlite)
    {
        var clock=new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T02:00:00Z"));await using var f=await Fixture.Create(sqlite,clock,builtInAuthority:true);
        var parent=await BackgroundParent(f);var child=await BackgroundChild(f,parent);var store=(ITenantRuntimeStore)f.Iam;await store.AdmitTenantImmediateAsync(parent.Frame,child.Session,child.Run,f.Ids.NewId());
        var first=await ClaimBackground(f,child.Run);clock.Advance(TimeSpan.FromMinutes(6));
        var recovered=await store.ApplyTenantRuntimeCommandAsync(first.Run.Owner,first.Run.AgentRunId,new AgentRunCommand.Recover(first.Run.Revision,clock.GetUtcNow()),f.Ids.NewId());
        var current=await ClaimBackground(f,recovered);var old=BackgroundOutcome(f,first.Run,first.Snapshot);
        await Assert.ThrowsAsync<AccessDeniedException>(()=>store.CommitTenantOutcomeAsync(first.Frame,old.Snapshot,first.Snapshot.Revision,old.Complete,null,f.Ids.NewId()).AsTask());
        var result=BackgroundOutcome(f,current.Run,current.Snapshot);await store.CommitTenantOutcomeAsync(current.Frame,result.Snapshot,current.Snapshot.Revision,result.Complete,null,f.Ids.NewId());
        var rootRun=(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(parent.Setup.Tenant,parent.Parent.AgentRunId))!;
        await store.ApplyTenantRuntimeCommandAsync(rootRun.Owner,rootRun.AgentRunId,new AgentRunCommand.Recover(rootRun.Revision,clock.GetUtcNow()),f.Ids.NewId());
        var waiting=(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(parent.Setup.Tenant,rootRun.AgentRunId))!;
        await store.ApplyTenantRuntimeCommandAsync(waiting.Owner,waiting.AgentRunId,new AgentRunCommand.RequestCancellation(waiting.Revision,clock.GetUtcNow(),"test"),f.Ids.NewId());
        Assert.Equal(child.Run.AgentRunId,Assert.Single(await store.ListTenantBackgroundCompletionsAsync(100)).Child.AgentRunId);
    }
    [Fact]
    public async Task Tenant_background_nullable_receipt_migration_has_empty_down_up_parity()
    {
        await using var f=await Fixture.Create(true,builtInAuthority:true);await using var db=f.Contexts!.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261020090000_TenantAgentDecommission");await db.Database.MigrateAsync();Assert.False(db.Database.HasPendingModelChanges());
        var checks=await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync();Assert.Equal(0,checks);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_background_requester_uses_original_scoped_conversation_after_logout_and_loses_it_on_read_revocation(bool sqlite)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true);var original=await BackgroundParent(f);await CompleteBackgroundParent(f,original);
        async Task<long> Revision()=>(await f.Iam.ReadTenantAsync(original.Setup.Tenant))!.Tenant.Revision;
        var role=await f.Access.SaveRoleAsync(original.Setup.Owner.PrincipalId,original.Setup.Tenant,null,"Scoped requester creation",["sessions.create","agent-runs.create"],await Revision());
        var account=await f.Identity.RegisterAsync("background-reader@example.test","Scoped requester","strong-requester-password");
        var invite=await f.Access.InviteAsync(original.Setup.Owner.PrincipalId,original.Setup.Tenant,account.LoginKey,role.RoleId,await Revision());
        var user=await f.Access.AcceptInvitationAsync(account.AccountId,original.Setup.Tenant,invite.Invitation.InvitationId,invite.Token);
        var login=(await f.Identity.SignInAsync(account.LoginKey,"strong-requester-password"))!.Session;
        var member=new TenantSessionInitiator(account.AccountId,login.SessionId,user.PrincipalId);
        role=await f.Access.SaveRoleAsync(original.Setup.Owner.PrincipalId,original.Setup.Tenant,null,"Exact parent read",["sessions.read"],await Revision());
        var read=await f.Access.AssignRoleAsync(original.Setup.Owner.PrincipalId,original.Setup.Tenant,user.PrincipalId,role.RoleId,TenantSessionRules.Scope(original.Setup.Tenant,original.Setup.Session),await Revision());
        role=await f.Access.SaveRoleAsync(original.Setup.Owner.PrincipalId,original.Setup.Tenant,null,"Exact chat",["agents.chat"],await Revision());
        await f.Access.AssignRoleAsync(original.Setup.Owner.PrincipalId,original.Setup.Tenant,user.PrincipalId,role.RoleId,AccessScope.ForResource(original.Setup.Tenant,"agent",original.Parent.AgentInstanceId.ToString("D")),await Revision());
        var metadata=(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(original.Setup.Tenant,original.Setup.Session))!;
        var message=await f.RunService().SubmitTextAsync(original.Setup.Tenant,original.Setup.Session,member,f.Ids.NewId(),"Scoped background request",await Revision(),metadata.Snapshot.Revision);
        var source=(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(original.Setup.Tenant,message.RunId))!;var claimedSource=await ClaimBackground(f,source);
        var parent=original with{Parent=claimedSource.Run,Frame=claimedSource.Frame,Snapshot=claimedSource.Snapshot};
        var child=await BackgroundChild(f,parent);var store=(ITenantRuntimeStore)f.Iam;await store.AdmitTenantImmediateAsync(parent.Frame,child.Session,child.Run,f.Ids.NewId());
        await f.Identity.SignOutAsync(member.AuthenticationSessionId);var claimed=await ClaimBackground(f,child.Run);
        Assert.False((await f.Authorizer.CheckAsync(member.PrincipalId,"sessions.read",TenantSessionRules.Scope(parent.Setup.Tenant,child.Session.SessionId))).Allowed);
        var authority=RequesterAuthority(f);Assert.NotNull(await authority.CurrentDefinitionAsync(claimed.Run));
        Assert.Equal("Scoped requester",(await authority.CurrentRequesterAsync(claimed.Run)).DisplayName);
        await f.Access.RevokeAssignmentAsync(parent.Setup.Owner.PrincipalId,parent.Setup.Tenant,read.AssignmentId,await Revision());
        Assert.Null(await authority.CurrentDefinitionAsync(claimed.Run));await Assert.ThrowsAsync<AccessDeniedException>(()=>authority.CurrentRequesterAsync(claimed.Run).AsTask());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_background_queued_report_yields_to_later_user_input_and_keeps_its_admitted_history_boundary(bool sqlite)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true);var parent=await BackgroundParent(f);await WaitReadAuthority(f,parent);var child=await BackgroundChild(f,parent);var store=(ITenantRuntimeStore)f.Iam;
        await store.AdmitTenantImmediateAsync(parent.Frame,child.Session,child.Run,f.Ids.NewId());var claimed=await ClaimBackground(f,child.Run);var outcome=BackgroundOutcome(f,claimed.Run,claimed.Snapshot);
        await store.CommitTenantOutcomeAsync(claimed.Frame,outcome.Snapshot,claimed.Snapshot.Revision,outcome.Complete,null,f.Ids.NewId());await CompleteBackgroundParent(f,parent);
        var source=Assert.Single(await store.ListTenantBackgroundCompletionsAsync(100));var report=await PrepareBackgroundReport(f,source);await store.AdmitTenantBackgroundCompletionAsync(report,source.Parent!.Snapshot.Revision,f.Ids.NewId());
        var state=(await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!;var session=(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(parent.Setup.Tenant,parent.Setup.Session))!;
        var later=await f.RunService().SubmitTextAsync(parent.Setup.Tenant,parent.Setup.Session,parent.Setup.Owner,f.Ids.NewId(),"Later user message",state.Tenant.Revision,session.Snapshot.Revision);
        await Assert.ThrowsAsync<AgentCoreException>(()=>ClaimBackground(f,report));
        var user=await ClaimBackground(f,(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(parent.Setup.Tenant,later.RunId))!);
        await store.ApplyTenantRuntimeCommandAsync(user.Run.Owner,user.Run.AgentRunId,new AgentRunCommand.Complete(user.Run.Revision,f.Clock.GetUtcNow(),user.Frame.Generation,"Later handled",AgentRunOutcomeKind.NoAction,null),f.Ids.NewId());
        var delivering=await ClaimBackground(f,report);Assert.DoesNotContain(delivering.Snapshot.Entries,e=>e.Text=="Later user message");
        Assert.Equal(2,delivering.Snapshot.DurableLastEntrySequence);
        var result=BackgroundOutcome(f,delivering.Run,delivering.Snapshot);await store.CommitTenantOutcomeAsync(delivering.Frame,result.Snapshot,delivering.Snapshot.Revision,result.Complete,null,f.Ids.NewId());
        Assert.Equal(3,(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(parent.Setup.Tenant,parent.Setup.Session))!.Snapshot.DurableLastEntrySequence);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_background_human_cancellation_creates_one_factual_completion_and_failed_report_is_not_replayed(bool sqlite)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true);var parent=await BackgroundParent(f);await WaitReadAuthority(f,parent);var child=await BackgroundChild(f,parent);var store=(ITenantRuntimeStore)f.Iam;
        await store.AdmitTenantImmediateAsync(parent.Frame,child.Session,child.Run,f.Ids.NewId());await CompleteBackgroundParent(f,parent);
        var revision=(await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision;
        var cancelled=await f.RunService().CancelAsync(parent.Setup.Tenant,child.Run.AgentRunId,parent.Setup.Owner,revision,child.Run.Revision);Assert.Equal(AgentRunStatus.Cancelled,cancelled.Status);
        var source=Assert.Single(await store.ListTenantBackgroundCompletionsAsync(100));var report=await PrepareBackgroundReport(f,source);await store.AdmitTenantBackgroundCompletionAsync(report,source.Parent!.Snapshot.Revision,f.Ids.NewId());
        var claimed=await ClaimBackground(f,report);await store.ApplyTenantRuntimeCommandAsync(claimed.Run.Owner,claimed.Run.AgentRunId,
            new AgentRunCommand.Fail(claimed.Run.Revision,f.Clock.GetUtcNow(),claimed.Frame.Generation,"controlled-failure","Controlled report failure",false,null,f.Ids.NewId()),f.Ids.NewId());
        Assert.Empty(await store.ListTenantBackgroundCompletionsAsync(100));
        if(sqlite){await using var db=f.Contexts!.CreateDbContext();var receipt=await db.BackgroundCompletionReceipts.SingleAsync(r=>r.ChildAgentRunId==child.Run.AgentRunId.ToString("D"));Assert.Equal((int)CompletionInboxStatus.Skipped,receipt.Status);Assert.Equal("report-unavailable",receipt.SkipReason);}
    }

}
