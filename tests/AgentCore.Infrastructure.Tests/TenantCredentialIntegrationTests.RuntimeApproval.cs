using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    private sealed record RuntimeCredentialSetup(Guid Tenant, TenantSessionInitiator Owner, TenantAgent Agent, Guid CredentialId, AgentRun Run);
    private static TenantApprovalService RuntimeApprovals(Fixture f, IIamStore? security = null)
    {
        var current = security ?? f.Iam; var iam = new IamAuthorizer(current);
        return new(current, (ITenantApprovalStore)current, (ITenantAgentStore)current,
            new(current, (ITenantRunStore)current, (ITenantAgentStore)current, iam, new((ITenantAuthorizationAuditStore)current, f.Ids, f.Clock)),
            iam, f.Identity, f.Ids, f.Clock, f.RunConfiguration(), ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default,
            (ITenantApprovedOperationStore)current, new((ITenantCredentialBindingStore)current, f.RunConfiguration()));
    }
    private static async Task<RuntimeCredentialSetup> RuntimeCredential(Fixture f, bool use, CredentialPurpose purpose = CredentialPurpose.GoogleDriveAccount, bool password = false)
    {
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, owner.TenantId, "general-assistant", 32, await Revision());
        var credential = password
            ? (await f.Service.CreateAsync(human.PrincipalId, owner.TenantId, new("Custom mail", CredentialKind.Password, CredentialPurpose.EmailAccount,
                null, null, "private-mail-password", EmailAccountProfile.Custom("fixture@example.test")), await Revision())).Credential.CredentialId
            : await ConnectForCheck(f, owner.TenantId, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId), purpose);
        var value = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, credential))!.Value;
        await f.BindingService.BindAsync(human.PrincipalId, owner.TenantId, agent.Instance.InstanceId, credential, "work-drive", await Revision(), agent.Instance.Revision, value.Revision);
        agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, agent.Instance.InstanceId))!;
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Bound credential check", use ? ["credentials.read", "credentials.use"] : ["credentials.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, agent.PrincipalId, role.RoleId,
            AccessScope.ForResource(owner.TenantId, "credential", credential.ToString("D")), await Revision());
        var session = await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var admission = await f.RunService().SubmitTextAsync(owner.TenantId, session.Owner.SessionId, human, f.Ids.NewId(), "Check my bound Drive connection", await Revision(), 1);
        var queued = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.TenantId, admission.RunId))!; var at = f.Clock.GetUtcNow();
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        return new(owner.TenantId, human, agent, credential, run);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Runtime_credential_check_requires_exact_owned_binding_and_durable_use_review_without_settings_management(bool sqlite, bool use)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await RuntimeCredential(f, use); var service = RuntimeApprovals(f);
        var prepared = await service.PrepareCredentialCheckAsync(new(setup.Tenant, setup.Run.AgentRunId), "work-drive", setup.Run.Revision, setup.Run.Claim!.Generation);
        Assert.Equal(OperationDecisionKind.RequiresApproval, prepared.Decision); var request = Assert.IsType<TenantApprovalRequest>(prepared.Request);
        Assert.Equal(use ? ApprovalKind.Review : ApprovalKind.ExceptionalGrant, request.Kind);
        Assert.Equal("credentials.use", request.Permission); Assert.Equal("credential", request.ResourceType); Assert.Equal(setup.CredentialId.ToString("D"), request.ResourceId);
        Assert.Equal(TenantOperationKind.CredentialConnectionCheck, request.Operation.Kind); Assert.Null(request.Operation.Persona); Assert.Null(request.Operation.Preferences);
        Assert.NotNull(request.Snapshot!.Credential); Assert.Equal("work-drive", request.Operation.CredentialCheck!.Reference);
        Assert.DoesNotContain("original-private-refresh", JsonSerializer.Serialize(request)); Assert.DoesNotContain("ProtectedPayload", JsonSerializer.Serialize(request));
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, clock: f.Clock) : f.Iam;
        var view = await RuntimeApprovals(f, reopened).ReadViewAsync(setup.Tenant, request.RequestId, setup.Owner);
        Assert.Equal(request, view.Request); Assert.True(view.Review!.CanDecide); Assert.True(view.Review.CanCancel);
        Assert.Contains(view.Review.Changes, c => c.Field == "bindingReference" && Equals(c.After, "work-drive"));
        // Only an actual Running claim may originate a credential use. A saved wait cannot dispatch another check.
        var duplicate = await Assert.ThrowsAsync<AccessDeniedException>(() => service.PrepareCredentialCheckAsync(new(setup.Tenant, setup.Run.AgentRunId), "work-drive", setup.Run.Revision, setup.Run.Claim.Generation).AsTask());
        Assert.Equal("tenant_runtime_unavailable", duplicate.ReasonCode);
        var rejected = await RuntimeApprovals(f, reopened).DecideAsync(setup.Tenant, request.RequestId, setup.Owner, false,
            request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Equal(TenantApprovalStatus.Rejected, rejected.Status);
        Assert.Null(await ((ITenantApprovalStore)reopened).FindOperationGrantAsync(setup.Tenant, request.RequestId));
        Assert.Equal(1, await RuntimeApprovals(f, reopened).DispatchPendingAsync());
        Assert.Equal(0, await RuntimeApprovals(f, reopened).DispatchPendingAsync());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Credential_approval_for_Gmail_saves_only_exact_consent_and_cannot_enter_the_local_configuration_executor(bool sqlite, bool use)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await RuntimeCredential(f, use, CredentialPurpose.EmailAccount); var service = RuntimeApprovals(f);
        var before = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
        var request = (await service.PrepareCredentialCheckAsync(new(setup.Tenant, setup.Run.AgentRunId), "work-drive", setup.Run.Revision, setup.Run.Claim!.Generation)).Request!;
        var view = await service.ReadViewAsync(setup.Tenant, request.RequestId, setup.Owner);
        var approved = await service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Equal(TenantApprovalStatus.Approved, approved.Status);
        var grant = await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId);
        if (use) Assert.Null(grant);
        else { Assert.NotNull(grant); Assert.Equal(request.Operation.ActionHash, grant.ActionHash); Assert.Equal(OperationGrantStatus.Available, grant.Status); }
        Assert.Equal(1, await service.DispatchPendingAsync());
        var queued = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!; var at = f.Clock.GetUtcNow();
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        var iam = new IamAuthorizer(f.Iam);
        var local = new TenantApprovedOperationService(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, (ITenantApprovalStore)f.Iam,
            (ITenantApprovedOperationStore)f.Iam, new(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, iam, new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, f.Clock)),
            f.RunConfiguration(), f.Ids, f.Clock, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default);
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => local.ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId,
            request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash).AsTask());
        Assert.Equal("tenant_effect_unavailable", denied.ReasonCode);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(setup.Tenant, request.Operation.OperationId));
        var after = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
        Assert.Equal(before.Revision, after.Revision); Assert.Equal(before.SecretRevision, after.SecretRevision);
        Assert.Equal(TenantCredentialOAuthRules.Epoch(before), TenantCredentialOAuthRules.Epoch(after)); Assert.True(before.ProtectedPayload == after.ProtectedPayload);
        var agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(setup.Tenant, setup.Agent.Instance.InstanceId))!.Instance;
        Assert.Equal(setup.Agent.Instance.Revision, agent.Revision); Assert.Equal(setup.Agent.Instance.Persona, agent.Persona);
        Assert.Equal(grant, await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId));
        Assert.Contains(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Execution?.Permission == "credentials.use" && a.ReasonCode == "tenant_effect_unavailable");
    }

    [Theory]
    [InlineData(false, "rotate")] [InlineData(true, "rotate")]
    [InlineData(false, "read")] [InlineData(true, "read")]
    public async Task Credential_review_rechecks_secret_witness_and_executor_read_but_preserves_cancellation(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await RuntimeCredential(f, true); var service = RuntimeApprovals(f);
        var request = (await service.PrepareCredentialCheckAsync(new(setup.Tenant, setup.Run.AgentRunId), "work-drive", setup.Run.Revision, setup.Run.Claim!.Generation)).Request!;
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        if (boundary == "rotate")
        {
            var credential = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
            var replacement = f.Protector.Protect(setup.CredentialId, checked((int)credential.SecretRevision + 1),
                GoogleOAuthRefreshGrant.Write("rotated-private-refresh", "tenant-client"));
            Assert.True(await ((ITenantCredentialOAuthStore)f.Iam).TryCheckCredentialOAuthAsync(Next(state), state.Tenant.Revision, setup.CredentialId,
                new(setup.Owner.AccountId, setup.Owner.AuthenticationSessionId, setup.Owner.PrincipalId), credential.Revision, credential.SecretRevision,
                new(replacement), f.Audit(setup.Tenant, setup.Owner.PrincipalId, setup.CredentialId)));
        }
        else
        {
            var role = state.Roles.Single(r => r.Name == "Bound credential check");
            await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, role.RoleId, role.Name, ["credentials.use"], state.Tenant.Revision);
        }
        var view = await service.ReadViewAsync(setup.Tenant, request.RequestId, setup.Owner);
        var reason = boundary == "rotate" ? "operation_witness_changed" : "executor_read_required";
        Assert.False(view.Review!.CanDecide); Assert.True(view.Review.CanCancel); Assert.Equal(reason, view.Review.DecisionUnavailableReason);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true,
            request.Operation.ActionHash, view.SecurityRevision, request.Revision).AsTask());
        Assert.Equal(reason, error is AccessDeniedException denied ? denied.ReasonCode : Assert.IsType<SecurityConflictException>(error).ReasonCode);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId));
        Assert.Equal(TenantApprovalStatus.Cancelled, (await service.CancelAsync(setup.Tenant, request.RequestId, setup.Owner,
            request.Operation.ActionHash, view.SecurityRevision, request.Revision)).Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Credential_preparation_denies_missing_executor_read_and_unbound_alias_without_saving_a_request(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await RuntimeCredential(f, true); var service = RuntimeApprovals(f);
        var reference = new TenantRunReference(setup.Tenant, setup.Run.AgentRunId);
        var unbound = await Assert.ThrowsAsync<AccessDeniedException>(() => service.PrepareCredentialCheckAsync(reference, "other-drive", setup.Run.Revision, setup.Run.Claim!.Generation).AsTask());
        Assert.Equal("credential_unavailable", unbound.ReasonCode);
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!; var role = state.Roles.Single(r => r.Name == "Bound credential check");
        await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, role.RoleId, role.Name, ["credentials.use"], state.Tenant.Revision);
        var result = await service.PrepareCredentialCheckAsync(reference, "work-drive", setup.Run.Revision, setup.Run.Claim!.Generation);
        Assert.Equal(OperationDecisionKind.Denied, result.Decision); Assert.Equal("executor_read_required", result.ReasonCode); Assert.Null(result.Request);
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(setup.Tenant, null, null, new())).Items);
        Assert.Contains(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Execution?.Permission == "credentials.use" && a.ReasonCode == "executor_read_required");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Credential_approval_inventory_requires_both_scoped_read_and_use_and_dedupes_mutable_witnesses(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await RuntimeCredential(f, true); var service = RuntimeApprovals(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var request = (await service.PrepareCredentialCheckAsync(new(setup.Tenant, setup.Run.AgentRunId), "work-drive", setup.Run.Revision, setup.Run.Claim!.Generation)).Request!;
        var account = await f.Identity.RegisterAsync("reviewer@example.test", "Reviewer", "strong-reviewer-password");
        var inbox = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Read approvals", ["approvals.read"], await Revision());
        var invited = await f.Access.InviteAsync(setup.Owner.PrincipalId, setup.Tenant, account.LoginKey, inbox.RoleId, await Revision());
        var member = await f.Access.AcceptInvitationAsync(account.AccountId, setup.Tenant, invited.Invitation.InvitationId, invited.Token);
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-reviewer-password"))!.Session;
        var human = new TenantSessionInitiator(account.AccountId, login.SessionId, member.PrincipalId);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Review exact credential", ["credentials.use"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, role.RoleId,
            AccessScope.ForResource(setup.Tenant, "credential", setup.CredentialId.ToString("D")), await Revision());
        Assert.Equal(0, (await service.ListAsync(setup.Tenant, human, new())).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadViewAsync(setup.Tenant, request.RequestId, human).AsTask());
        await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, role.RoleId, role.Name, ["credentials.read", "credentials.use"], await Revision());
        var visible = await service.ListAsync(setup.Tenant, human, new(0, 1)); Assert.Equal(1, visible.Total);
        Assert.Equal(request.RequestId, Assert.Single(visible.Items).Request.RequestId);
        var changed = TenantApprovalRules.PrepareCredentialCheck(f.Ids.NewId(), request.Operation.InstanceId,
            request.Operation.CredentialCheck! with { SecretRevision = request.Operation.CredentialCheck!.SecretRevision + 1 },
            request.Operation.ExpectedInstanceRevision + 1, request.Operation.ExpectedPersonaRevision);
        Assert.NotEqual(request.Operation.ActionHash, changed.ActionHash); Assert.Equal(request.Operation.OperationFingerprint, changed.OperationFingerprint);
        Assert.Equal(request, await ((ITenantApprovalStore)f.Iam).FindRootApprovalAsync(setup.Tenant, request.RootActivationId, request.ExecutorPrincipalId, changed));
    }

    [Fact]
    public async Task Credential_approval_migration_preserves_saved_local_requests_and_refuses_lossy_downgrade()
    {
        await using var f = await Fixture.Create(true); var setup = await RuntimeCredential(f, true); var service = RuntimeApprovals(f);
        var local = (await service.PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Agent.Instance.Persona with { Name = "Retained review" },
            setup.Run.Revision, setup.Run.Claim!.Generation)).Request!;
        local = await service.DecideAsync(setup.Tenant, local.RequestId, setup.Owner, true, local.Operation.ActionHash,
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, local.Revision);
        var retainedGrant = await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, local.RequestId);
        Assert.NotNull(retainedGrant);
        await using (var db = await f.Contexts!.CreateDbContextAsync())
        {
            var migrator = db.GetService<IMigrator>();
            // Current Runs retain public-download references. Roundtrip the safe
            // predecessor, then require the deeper lossy downgrade to preserve them.
            await migrator.MigrateAsync("20261023180000_TenantPublicDownloads");
            await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync("20261017071356_TenantExecutionAuthorizationAudit"));
            Assert.Contains("20261023180000_TenantPublicDownloads", await db.Database.GetAppliedMigrationsAsync());
            await migrator.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        }
        Assert.Equal(local, await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(setup.Tenant, local.RequestId));
        Assert.Equal(retainedGrant, await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, local.RequestId));
        Assert.Equal(1, await service.DispatchPendingAsync());
        var queued = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!; var at = f.Clock.GetUtcNow();
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        var check = (await service.PrepareCredentialCheckAsync(new(setup.Tenant, run.AgentRunId), "work-drive", run.Revision, run.Claim!.Generation)).Request!;
        await using (var db = await f.Contexts!.CreateDbContextAsync())
        {
            var payload = await db.Database.SqlQuery<string>($"SELECT PayloadJson AS Value FROM TenantApprovalRequests WHERE RequestId={check.RequestId}").SingleAsync();
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261017071356_TenantExecutionAuthorizationAudit"));
            // Completed safe Down steps remain applied when an older lossy step refuses.
            Assert.Equal(payload, await db.Database.SqlQuery<string>($"SELECT PayloadJson AS Value FROM TenantApprovalRequests WHERE RequestId={check.RequestId}").SingleAsync());
            Assert.Contains("20261017090143_TenantCredentialApprovalTargets", await db.Database.GetAppliedMigrationsAsync());
            Assert.Contains("20261023180000_TenantPublicDownloads", await db.Database.GetAppliedMigrationsAsync());
            await db.GetService<IMigrator>().MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
        }
        Assert.Equal(check, await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(setup.Tenant, check.RequestId));
        await using (var db = await f.Contexts!.CreateDbContextAsync())
        {
            Assert.Contains("20261017090143_TenantCredentialApprovalTargets", await db.Database.GetAppliedMigrationsAsync());
            var foreign = Guid.NewGuid().ToString("D");
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET CredentialId={foreign}, ResourceId={foreign} WHERE RequestId={check.RequestId}"));
        }
        Assert.Equal(check, await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(setup.Tenant, check.RequestId));
    }
}
