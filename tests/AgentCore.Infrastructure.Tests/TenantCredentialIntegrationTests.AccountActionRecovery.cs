using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Account_abandoned_claim_recovery_consumes_exact_uncertainty_without_any_native_replay(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T12:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var s = await RuntimeCredential(f, true);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Raw account reads", ["sessions.read", "drive.read"], await Revision());
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), await Revision());
        var nativeCalls = 0;
        var http = new OAuthHttp(_ => { nativeCalls++; throw new InvalidOperationException("Recovery must not touch a provider."); });
        var services = ActionServices(f, http);
        var arguments = TenantAccountActionContract.Parse(s.Run.SessionId, ToolCatalog.DriveCreateFolder, """{"accountRef":"work-drive","authEpoch":1,"name":"Exact","parentId":"root"}""");
        var run = await AccountPendingCall(f, s.Run, AccountIntentCall(arguments.ToolName, arguments.Json));
        var request = (await services.Approvals.PrepareAccountActionAsync(new(s.Tenant, run.AgentRunId), new(arguments.ToolName, arguments.Json), run.Revision, run.Claim!.Generation)).Request!;
        Assert.Equal(ApprovalKind.ExceptionalGrant, request.Kind);
        run = await ApproveAccountContinuation(f, s, services, request);
        request = (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(s.Tenant, request.RequestId))!;
        var decision = (await ((ITenantApprovedOperationStore)f.Iam).FindApprovalDecisionAsync(s.Tenant, request.RequestId))!;
        var grant = (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId))!;
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, run.AgentRunId, run.Claim!.Generation, run.Revision, request.Revision,
            decision.DecisionId, grant.Revision, request.Operation.ActionHash, TenantOperationOutcome.Completed, "operation_completed");
        AuthorizationAudit Audit() => new(f.Ids.NewId(), s.Tenant, s.Agent.PrincipalId, TenantAccountEffectRules.DispatchAction,
            request.ResourceId, "allowed", "account_action_dispatched", clock.GetUtcNow());
        await Assert.ThrowsAsync<SecurityConflictException>(() => ((ITenantAccountOperationStore)f.Iam).TryStartAccountEffectAsync(Next(state), state.Tenant.Revision,
            commit with { Generation = f.Ids.NewId() }, Audit()).AsTask());
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId));
        var started = await ((ITenantAccountOperationStore)f.Iam).TryStartAccountEffectAsync(Next(state), state.Tenant.Revision, commit, Audit());
        Assert.NotNull(started!.Fence); Assert.Null(started.Receipt);
        Assert.Equal(OperationGrantStatus.Claimed, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId))!.Status);
        Assert.Equal(0, await services.Effects.RecoverAsync());
        clock.Advance(TimeSpan.FromMinutes(6));
        if (sqlite) services = ActionServices(f, http, new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock));
        Assert.Equal(1, await services.Effects.RecoverAsync()); Assert.Equal(0, await services.Effects.RecoverAsync()); Assert.Equal(0, nativeCalls);
        var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!;
        Assert.Equal(TenantOperationOutcome.Indeterminate, receipt.Outcome); Assert.Equal(TenantAccountEffectRules.Uncertain, receipt.ReasonCode);
        Assert.Equal(OperationGrantStatus.Indeterminate, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId))!.Status);
        Assert.Equal(receipt, await services.Effects.ExecuteApprovedAsync(new(s.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash));
        Assert.Equal(0, nativeCalls);
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext();
            var guardsBeforeVeto = await AccountConsentGuards(db);
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261020150000_TenantAccountReadReview"));
            // Earlier empty migrations may already be down; restore the current model after the populated-data veto.
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(guardsBeforeVeto, await AccountConsentGuards(db));
        }
    }
    [Fact]
    public async Task Account_action_empty_SQLite_down_up_retains_all_read_guards_and_current_shape()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, targetMigration: "20261020160000_TenantAccountActionReview");
        await using var db = f.Contexts!.CreateDbContext(); var migrator = db.GetService<IMigrator>();
        var currentGuards = await AccountConsentGuards(db);
        await migrator.MigrateAsync("20261020150000_TenantAccountReadReview");
        Assert.Equal(15, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_master WHERE type='trigger' AND (name LIKE 'TR_TenantApprovals_%' OR name LIKE 'TR_TenantReceipts_%')").SingleAsync());
        await migrator.MigrateAsync("20261020160000_TenantAccountActionReview");
        Assert.Equal(currentGuards, await AccountConsentGuards(db));
        await migrator.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Account_password_read_consent_accepts_epoch_zero_without_enabling_provider_io(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var s = await RuntimeCredential(f, true, AgentCore.Domain.Credentials.CredentialPurpose.EmailAccount, password: true);
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Custom source read", ["sessions.read", "email.read"], state.Tenant.Revision);
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var calls = 0; var http = new OAuthHttp(_ => { calls++; throw new InvalidOperationException("Consent must not read a provider."); });
        var services = ActionServices(f, http);
        var input = TenantAccountReadContract.Parse(ToolCatalog.EmailSearch, """{"accountRef":"work-drive","query":"exact","limit":1}""");
        var request = (await services.Approvals.PrepareAccountReadAsync(new(s.Tenant, s.Run.AgentRunId), new(ToolCatalog.EmailSearch, input.Json), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        Assert.Equal(0, request.Operation.AccountRead!.Credential.AuthEpoch); Assert.Equal("Configured", request.Snapshot!.Credential!.ConnectionStatus);
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, s.Owner);
        await services.Approvals.DecideAsync(s.Tenant, request.RequestId, s.Owner, false, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Equal(0, calls);
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext();
            var guardsBeforeVeto = await AccountConsentGuards(db);
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261020150000_TenantAccountReadReview"));
            // Earlier empty migrations may already be down; restore the current model after the populated-data veto.
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(guardsBeforeVeto, await AccountConsentGuards(db));
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public Task Account_password_draft_is_atomic_without_OAuth_epoch_or_provider_io(bool sqlite) =>
        Account_actions_use_owned_atomic_drafts_exact_successors_and_factual_native_receipts_without_replay(sqlite, "draft-password");

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public Task Account_action_final_output_rechecks_retained_result_authority_in_owning_transaction(bool sqlite, bool revoke) =>
        Account_actions_use_owned_atomic_drafts_exact_successors_and_factual_native_receipts_without_replay(sqlite, revoke ? "send-final-revoked" : "send-final-allowed");

    [Theory] [InlineData(false)] [InlineData(true)]
    public Task Account_send_preflight_refusal_records_no_dispatch_without_claiming_the_draft(bool sqlite) =>
        Account_actions_use_owned_atomic_drafts_exact_successors_and_factual_native_receipts_without_replay(sqlite, "send-preflight-refused");

}
