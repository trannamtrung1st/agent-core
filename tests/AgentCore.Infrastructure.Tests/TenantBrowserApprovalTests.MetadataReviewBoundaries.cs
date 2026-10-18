using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Credential_metadata_Review_inbox_filters_all_saved_scopes_before_totals_and_never_requires_Credential_use(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, password: true, credentialUseAllowed: false, passwordCount: 3);
        await RequireReadReview(f, s, "credentials.read"); var service = Service(f, Probe(s));
        var request = (await service.PrepareCredentialReadAsync(new(s.Tenant, s.Run.AgentRunId), "{\"limit\":1}", s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        var refs = TenantApprovalRules.CredentialReadReferences(request.Operation.CredentialRead!);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var account = await f.Identity.RegisterAsync("metadata-reviewer@example.test", "Metadata reviewer", "strong-reviewer-password");
        var invite = await f.Access.InviteAsync(s.Human.PrincipalId, s.Tenant, account.LoginKey,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Roles.Single(r => r.Key == "approver").RoleId, await Revision());
        var member = await f.Access.AcceptInvitationAsync(account.AccountId, s.Tenant, invite.Invitation.InvitationId, invite.Token);
        foreach (var assignment in (await f.Iam.ReadTenantAsync(s.Tenant))!.Assignments.Where(a => a.PrincipalId == member.PrincipalId))
            await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, await Revision());
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-reviewer-password"))!.Session;
        var human = new TenantSessionInitiator(account.AccountId, login.SessionId, member.PrincipalId);
        async Task Grant(string name, string[] permissions, AccessScope scope)
        {
            var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, name, permissions, await Revision());
            await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, member.PrincipalId, role.RoleId, scope, await Revision());
        }
        await Grant("Read review inbox", ["approvals.read"], AccessScope.ForTenant(s.Tenant));
        await Grant("Read exact Session", ["sessions.read", "approvals.approve"], TenantSessionRules.Scope(s.Tenant, s.Run.SessionId));
        await Grant("Read primary Credential", ["credentials.read", "approvals.approve"], TenantCredentialRules.Scope(s.Tenant, refs[0].CredentialId));
        Assert.Equal(0, (await service.ListAsync(s.Tenant, human, new(0, 1))).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadViewAsync(s.Tenant, request.RequestId, human).AsTask());
        var outside = (await ((ITenantCredentialBindingStore)f.Iam).ReadTenantCredentialBindingsAsync(s.Tenant, s.Agent.Instance.InstanceId))!.Bindings
            .Single(b => refs.All(r => r.BindingId != b.Binding.BindingId));
        await Grant("Read outside page", ["credentials.read", "approvals.approve"], TenantCredentialRules.Scope(s.Tenant, outside.Credential.Value.CredentialId));
        Assert.Equal(0, (await service.ListAsync(s.Tenant, human, new(0, 1))).Total);
        await Grant("Read saved lookahead", ["credentials.read", "approvals.approve"], TenantCredentialRules.Scope(s.Tenant, refs[1].CredentialId));
        Assert.Equal(1, (await service.ListAsync(s.Tenant, human, new(0, 1))).Total);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, new(f.Iam), f.Identity, f.Ids, f.Clock)
            .SaveAsync(s.Tenant, s.Human, "sessions.read", new(OperationApprovalMode.Always, true), await Revision(), 0);
        Assert.False((await service.ReadViewAsync(s.Tenant, request.RequestId, s.Human)).Review!.CanDecide);
        var view = await service.ReadViewAsync(s.Tenant, request.RequestId, human); Assert.True(view.Review!.CanDecide); Assert.True(view.Review.IndependentReview);
        Assert.False((await new IamAuthorizer(f.Iam).CheckTenantAsync((await f.Iam.ReadTenantAsync(s.Tenant))!, human.PrincipalId,
            PermissionCatalog.Get("credentials.use"), TenantCredentialRules.Scope(s.Tenant, refs[0].CredentialId), CancellationToken.None)).Allowed);
        await service.DecideAsync(s.Tenant, request.RequestId, human, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
        Assert.Equal(0, (await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new(),
            credentialReadVisibility: new([refs[0].CredentialId], null))).Total);
        Assert.Equal(0, (await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new(),
            credentialReadVisibility: new(null, []))).Total);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Credential_metadata_Review_semantic_root_cannot_change_scopes_or_revisions_to_repeat_the_same_page(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, password: true, credentialUseAllowed: false);
        await RequireReadReview(f, s, "credentials.read"); var service = Service(f, Probe(s));
        var request = (await service.PrepareCredentialReadAsync(new(s.Tenant, s.Run.AgentRunId), "{}", s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        var changed = TenantApprovalRules.PrepareCredentialRead(f.Ids.NewId(), s.Agent.Instance.InstanceId,
            request.Operation.CredentialRead! with { CredentialId = null, ReferencesJson = "[]" }, 99, 99);
        Assert.NotEqual(request.Operation.ActionHash, changed.ActionHash); Assert.Equal(request.Fingerprint, changed.OperationFingerprint);
        Assert.Equal(request.RequestId, (await ((ITenantApprovalStore)f.Iam).FindRootApprovalAsync(s.Tenant, request.RootActivationId, s.Agent.PrincipalId, changed))!.RequestId);
        await f.Service.ReplaceAsync(s.Human.PrincipalId, s.Tenant, s.Password!.Credential.Value.CredentialId, "rotated-private-password",
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Password.Credential.Value.Revision);
        var view = await service.ReadViewAsync(s.Tenant, request.RequestId, s.Human); Assert.False(view.Review!.CanDecide); Assert.True(view.Review.CanCancel);
        await service.CancelAsync(s.Tenant, request.RequestId, s.Human, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
    }

    [Fact]
    public async Task Credential_metadata_SQLite_upgrade_preserves_existing_requests_and_rejects_invalid_read_shapes()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); var s = await Prepare(f, password: true, credentialUseAllowed: false);
        var target = new EffectProbe(Probe(s)); var service = Service(f, target);
        var browser = (await Request(service, s)).Request!; var view = await service.ReadViewAsync(s.Tenant, browser.RequestId, s.Human);
        browser = await service.DecideAsync(s.Tenant, browser.RequestId, s.Human, true, browser.Operation.ActionHash, view.SecurityRevision, browser.Revision);
        var decision = await ((ITenantApprovedOperationStore)f.Iam).FindApprovalDecisionAsync(s.Tenant, browser.RequestId);
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var bytes = (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleAsync()).PayloadJson;
            await db.GetService<IMigrator>().MigrateAsync("20261018053723_TenantBrowserCommandReview"); await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges()); Assert.Equal(bytes, (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleAsync()).PayloadJson);
        }
        Assert.Equal(decision, await ((ITenantApprovedOperationStore)f.Iam).FindApprovalDecisionAsync(s.Tenant, browser.RequestId));
        Assert.Single(await ((ITenantApprovalStore)f.Iam).PendingApprovalSignalsAsync(100));
        await service.DispatchPendingAsync(); var queued = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentCore.Application.Execution.AgentRunCommand.Claim(queued.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        s = s with { Run = run }; Assert.Equal(TenantOperationOutcome.Completed, (await ExecuteEffect(Effects(f, target), s, browser)).Receipt.Outcome);
        run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, run.AgentRunId))!; s = s with { Run = run };
        await RequireReadReview(f, s, "credentials.read");
        var request = (await service.PrepareCredentialReadAsync(new(s.Tenant, run.AgentRunId), "{}", run.Revision, run.Claim!.Generation)).Request!;
        await using (var db = f.Contexts!.CreateDbContext())
        {
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET Kind=1 WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.CredentialRead.SessionId',{f.Ids.NewId().ToString("D")}) WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.CredentialRead.ReferencesJson','[]') WHERE RequestId={request.RequestId}"));
            await db.Database.OpenConnectionAsync(); using var check = db.Database.GetDbConnection().CreateCommand(); check.CommandText = "PRAGMA foreign_key_check";
            using var reader = await check.ExecuteReaderAsync(); Assert.False(await reader.ReadAsync());
        }
    }
}
