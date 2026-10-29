using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;
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
    public async Task Tenant_Browser_Review_filters_scope_before_totals_and_rechecks_secondary_independent_review(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = Probe(s); var service = Service(f, probe);
        var request = (await Request(service, s)).Request!;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var account = await f.Identity.RegisterAsync("browser-reviewer@example.test", "Scoped reviewer", "strong-reviewer-password");
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var invite = await f.Access.InviteAsync(s.Human.PrincipalId, s.Tenant, account.LoginKey, state.Roles.Single(r => r.Key == "approver").RoleId, await Revision());
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, s.Tenant, invite.Invitation.InvitationId, invite.Token);
        foreach (var assignment in (await f.Iam.ReadTenantAsync(s.Tenant))!.Assignments.Where(a => a.PrincipalId == user.PrincipalId))
            await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, await Revision());
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-reviewer-password"))!.Session;
        var reviewer = new TenantSessionInitiator(account.AccountId, login.SessionId, user.PrincipalId);
        async Task<RoleAssignment> Grant(string name, string[] keys, AccessScope scope)
        {
            var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, name, keys, await Revision());
            return await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, user.PrincipalId, role.RoleId, scope, await Revision());
        }
        async Task Hidden()
        {
            var page = await service.ListAsync(s.Tenant, reviewer, new(0, 1)); Assert.Equal(0, page.Total); Assert.Empty(page.Items);
            Assert.Equal("approval_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadViewAsync(s.Tenant, request.RequestId, reviewer).AsTask())).ReasonCode);
        }
        await Grant("Read review inbox", ["approvals.read"], AccessScope.ForTenant(s.Tenant));
        var scope = AccessScope.ForResource(s.Tenant, "agent", s.Agent.Instance.InstanceId.ToString("D"));
        await Grant("Use exact Browser", ["browser.use"], scope); await Hidden();
        var sibling = await f.Agents.CreateAsync(s.Human.PrincipalId, s.Tenant, "examiner", 1, await Revision());
        await Grant("Read sibling Browser", ["browser.read"], AccessScope.ForResource(s.Tenant, "agent", sibling.Instance.InstanceId.ToString("D"))); await Hidden();
        await Grant("Read exact Browser", ["browser.read"], scope); await Hidden();
        var session = await Grant("Read exact Session", ["sessions.read"], TenantSessionRules.Scope(s.Tenant, s.Run.SessionId));
        var visible = await service.ListAsync(s.Tenant, reviewer, new(0, 1)); Assert.Equal(1, visible.Total); Assert.Single(visible.Items); Assert.False(visible.Items[0].Review!.CanDecide);
        await Grant("Review Browser use", ["approvals.approve"], scope);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(s.Tenant, s.Human, "sessions.read", new(OperationApprovalMode.Always, true), await Revision(), 0);
        var insufficient = await service.ReadViewAsync(s.Tenant, request.RequestId, reviewer); Assert.False(insufficient.Review!.CanDecide);
        var own = await service.ReadViewAsync(s.Tenant, request.RequestId, s.Human); Assert.False(own.Review!.CanDecide); Assert.True(own.Review.IndependentReview);
        await Grant("Review Session read", ["approvals.approve"], TenantSessionRules.Scope(s.Tenant, s.Run.SessionId));
        var qualified = await service.ReadViewAsync(s.Tenant, request.RequestId, reviewer); Assert.True(qualified.Review!.CanDecide); Assert.True(qualified.Review.IndependentReview);
        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, session.AssignmentId, await Revision()); await Hidden();
        await Grant("Restore exact Session read", ["sessions.read"], TenantSessionRules.Scope(s.Tenant, s.Run.SessionId));
        qualified = await service.ReadViewAsync(s.Tenant, request.RequestId, reviewer);
        await service.DecideAsync(s.Tenant, request.RequestId, reviewer, true, request.Operation.ActionHash, qualified.SecurityRevision, request.Revision);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_read_Review_requires_no_use_grant_and_busy_preflight_has_no_approval_side_effect(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, permissions: false); var probe = Probe(s); var service = Service(f, probe);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(s.Tenant, s.Human, "browser.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        probe.Error = "browser_target_busy";
        Assert.Equal("browser_target_busy", (await Assert.ThrowsAsync<AccessDeniedException>(() => Request(service, s, ToolCatalog.BrowserSnapshot, "{}").AsTask())).ReasonCode);
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new())).Items);
        probe.Error = null;
        var request = (await Request(service, s, ToolCatalog.BrowserSnapshot, "{}")).Request!;
        Assert.Equal("browser.read", request.Permission); Assert.True(request.Operation.BrowserCommand!.ReadOnly);
        var view = await service.ReadViewAsync(s.Tenant, request.RequestId, s.Human); Assert.True(view.Review!.CanDecide);
        await service.DecideAsync(s.Tenant, request.RequestId, s.Human, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
    }

    [Fact]
    public async Task Tenant_Browser_SQLite_upgrade_preserves_older_request_bytes_and_enforces_Browser_payload_constraints()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); var s = await Prepare(f); var probe = Probe(s); var service = Service(f, probe);
        var prior = (await service.PreparePersonaAsync(new(s.Tenant, s.Run.AgentRunId), s.Agent.Instance.Persona with { Name = "Earlier request" }, s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        await HistoricalTenantDatabase.AssertApprovalUpgradeAsync(f.Contexts!,
            "20261018025233_TenantSandboxReadReview", "20261018053723_TenantBrowserCommandReview",
            HistoricalTenantDatabase.CredentialAdmissionTables);
        await service.CancelAsync(s.Tenant, prior.RequestId, s.Human, prior.Operation.ActionHash, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, prior.Revision);
        Assert.Equal(1, await service.DispatchPendingAsync());
        var resumed = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!; var at = f.Clock.GetUtcNow();
        resumed = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(resumed.Owner, resumed.AgentRunId,
            new AgentRunCommand.Claim(resumed.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        s = s with { Run = resumed };
        var request = (await Request(service, s)).Request!;
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var row = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleAsync(r => r.RequestId == request.RequestId); var payload = row.PayloadJson;
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET Kind=1 WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.BrowserCommand.ReadOnly',json('true')) WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.BrowserCommand.SessionId',{Guid.NewGuid().ToString("D")}) WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.BrowserCommand.TargetJson','not-json') WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.Preferences',json('{{}}')) WHERE RequestId={request.RequestId}"));
            Assert.Equal(payload, (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleAsync(r => r.RequestId == request.RequestId)).PayloadJson);
            await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA foreign_key_check";
            using var reader = await command.ExecuteReaderAsync(); Assert.False(await reader.ReadAsync());
        }
        Assert.Equal(request, await new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns).FindApprovalAsync(s.Tenant, request.RequestId));
    }

    [Fact]
    public void Tenant_Browser_intent_classification_and_canonical_rebuild_reject_forged_or_unconverted_adapters()
    {
        var session = Guid.NewGuid();
        Assert.False(TenantApprovalRules.BrowserIntent(session, ToolCatalog.BrowserNavigate, Navigate).ReadOnly);
        Assert.False(TenantApprovalRules.BrowserIntent(session, ToolCatalog.BrowserClose, "{}").ReadOnly);
        Assert.False(TenantApprovalRules.BrowserIntent(session, ToolCatalog.BrowserTabs, """{"operation":"new","url":"http://127.0.0.1:5091"}""").ReadOnly);
        Assert.True(TenantApprovalRules.BrowserIntent(session, ToolCatalog.BrowserTabs, """{"operation":"list"}""").ReadOnly);
        Assert.True(TenantApprovalRules.BrowserIntent(session, ToolCatalog.BrowserDialog, """{"operation":"inspect"}""").ReadOnly);
        foreach (var args in new[] { "{", "[]", """{"url":"http://127.0.0.1:5091","url":"http://other.test"}""", """{"url":"http://127.0.0.1:5091","tenantId":"forged"}""" })
            Assert.Throws<ArgumentException>(() => TenantApprovalRules.BrowserIntent(session, ToolCatalog.BrowserNavigate, args));
        Assert.False(TenantApprovalRules.BrowserIntent(session, ToolCatalog.BrowserScreenshot, "{}").ReadOnly);
        var denied = Assert.Throws<AccessDeniedException>(() => TenantApprovalRules.BrowserIntent(session, ToolCatalog.BrowserGeolocation, """{"operation":"clear","origin":"http://127.0.0.1:5091"}"""));
        Assert.Equal("browser_resource_adapter_unavailable", denied.ReasonCode);
        Assert.False(TenantApprovalRules.BrowserOrigin("http://user:secret@example.test")); Assert.False(TenantApprovalRules.BrowserOrigin("http://example.test/private?q=secret"));
        Assert.True(TenantApprovalRules.BrowserOrigin("https://example.test"));
        var intent = TenantApprovalRules.BrowserIntent(session, ToolCatalog.BrowserNavigate, Navigate);
        var value = new PreparedBrowserCommand(session, ToolCatalog.BrowserNavigate, intent.Arguments, BrowserCommandIdentity.Hash(intent.Command),
            JsonSerializer.Serialize(new BrowserReviewTarget(BrowserProfileMode.EphemeralSession, null, null, null, null, null, 0, null)), false);
        var prepared = TenantApprovalRules.PrepareBrowser(Guid.NewGuid(), Guid.NewGuid(), value, 1, 1);
        Assert.Equal(prepared, TenantApprovalRules.Rebuild(prepared));
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.PrepareBrowser(prepared.OperationId, prepared.InstanceId, value with { ReadOnly = true }, 1, 1));
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.PrepareBrowser(prepared.OperationId, prepared.InstanceId, value with { CommandHash = new string('0', 64) }, 1, 1));
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.BrowserTarget(value with { TargetJson = "[]" }));
        Assert.Contains("Operation: goto", TenantApprovalRules.BrowserDescription(value));
        Assert.DoesNotContain("{", TenantApprovalRules.BrowserDescription(value));
    }
}
