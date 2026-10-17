using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    private static TenantCredentialCheckService RuntimeChecks(Fixture f, ITenantCredentialOAuthVerifier verifier, IIamStore? current = null, ITenantCredentialCheckStore? checkStore = null)
    {
        var store = current ?? f.Iam; var iam = new IamAuthorizer(store);
        return new(store, (ITenantRunStore)store, (ITenantApprovalStore)store, (ITenantApprovedOperationStore)store, checkStore ?? (ITenantCredentialCheckStore)store,
            new((ITenantCredentialBindingStore)store, f.RunConfiguration()),
            new(store, (ITenantRunStore)store, (ITenantAgentStore)store, iam, new((ITenantAuthorizationAuditStore)store, f.Ids, f.Clock)), iam, verifier, f.Ids, f.Clock);
    }
    private static async Task<(RuntimeCredentialSetup Setup, TenantApprovalRequest Request, AgentRun Run)> ApprovedRuntimeCheck(Fixture f, bool use = false,
        CredentialPurpose purpose = CredentialPurpose.GoogleDriveAccount)
    {
        var setup = await RuntimeCredential(f, use, purpose); var service = RuntimeApprovals(f);
        var request = (await service.PrepareCredentialCheckAsync(new(setup.Tenant, setup.Run.AgentRunId), "work-drive", setup.Run.Revision, setup.Run.Claim!.Generation)).Request!;
        var view = await service.ReadViewAsync(setup.Tenant, request.RequestId, setup.Owner);
        request = await service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Equal(1, await service.DispatchPendingAsync());
        var queued = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!; var at = f.Clock.GetUtcNow();
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        return (setup, request, run);
    }
    private sealed class RuntimeCheckVerifier(Func<Credential, CancellationToken, ValueTask<CredentialOAuthCheck>> check) : ITenantCredentialOAuthVerifier
    {
        public int Calls;
        public bool HostEnabled = true;
        public CredentialOAuthDescriptor Describe(CredentialPurpose purpose) => new("tenant-client", "https://app.example.test/callback", false, true, HostEnabled);
        public ValueTask<CredentialOAuthCheck> CheckAsync(Credential current, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return check(current, ct); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Approved_runtime_credential_check_rotates_protected_grant_once_with_durable_receipt_and_reopen(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var (setup, request, run) = await ApprovedRuntimeCheck(f);
        var before = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
        var verifier = new RuntimeCheckVerifier((credential, _) => {
            Assert.Equal(setup.CredentialId, credential.CredentialId);
            Assert.Contains("original-private-refresh", f.Protector.Unprotect(credential.CredentialId, credential.ProtectionVersion, credential.ProtectedPayload));
            return ValueTask.FromResult(new CredentialOAuthCheck(f.Protector.Protect(credential.CredentialId, credential.ProtectionVersion,
                GoogleOAuthRefreshGrant.Write("runtime-rotated-private-refresh", "tenant-client"))));
        });
        await f.Identity.SignOutAsync(setup.Owner.AuthenticationSessionId);
        var receipt = await RuntimeChecks(f, verifier).ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId,
            request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome); Assert.Equal("credential_connection_checked", receipt.ReasonCode); Assert.Equal(1, verifier.Calls);
        var after = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
        Assert.Equal(before.Revision, after.Revision); Assert.Equal(before.SecretRevision + 1, after.SecretRevision);
        Assert.Contains("runtime-rotated-private-refresh", f.Protector.Unprotect(after.CredentialId, after.ProtectionVersion, after.ProtectedPayload));
        Assert.Equal(DriveConnectionStatus.Connected, after.DriveAccount!.ConnectionStatus);
        var grant = (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId))!;
        Assert.Equal(OperationGrantStatus.Consumed, grant.Status); Assert.Equal(3, grant.Revision);
        var saved = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!;
        Assert.Equal(AgentRunSideEffectDisposition.Succeeded, saved.SideEffect.Disposition);
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, clock: f.Clock) : f.Iam;
        Assert.Equal(receipt, await RuntimeChecks(f, verifier, reopened).ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId,
            request.Operation.OperationId, run.Claim.Generation, request.Operation.ActionHash));
        Assert.Equal(1, verifier.Calls);
        var dispatch = (await ((ITenantCredentialCheckStore)reopened).FindCredentialCheckDispatchAsync(setup.Tenant, request.Operation.OperationId))!;
        Assert.NotNull(dispatch.CompletedAt); Assert.Equal(run.Claim.Generation, dispatch.Generation);
        var audits = await reopened.ReadAuditAsync(setup.Tenant, 200);
        Assert.Single(audits, a => a.Action == TenantCredentialCheckRules.DispatchAction);
        var effect = Assert.Single(audits, a => a.Action == TenantCredentialCheckRules.Action);
        Assert.Equal("credentials.use", effect.Execution!.Permission); Assert.Equal(request.Operation.OperationId, effect.Execution.OperationId);
        Assert.DoesNotContain("private-refresh", JsonSerializer.Serialize(receipt)); Assert.DoesNotContain("ProtectedPayload", JsonSerializer.Serialize(audits));
    }

    [Theory]
    [InlineData(false, CredentialPurpose.GoogleDriveAccount)] [InlineData(true, CredentialPurpose.GoogleDriveAccount)]
    [InlineData(false, CredentialPurpose.EmailAccount)] [InlineData(true, CredentialPurpose.EmailAccount)]
    public async Task Runtime_credential_check_uses_the_actual_bounded_Google_adapter_without_repeating_refresh(bool sqlite, CredentialPurpose purpose)
    {
        await using var f = await Fixture.Create(sqlite); var (setup, request, run) = await ApprovedRuntimeCheck(f, true, purpose); var calls = 0;
        using var http = new OAuthHttp(async outgoing => {
            Interlocked.Increment(ref calls);
            if (outgoing.RequestUri!.Host == "oauth2.googleapis.com")
            {
                Assert.Contains("original-private-refresh", await outgoing.Content!.ReadAsStringAsync());
                return OAuthJson(new { access_token = "runtime-private-access", refresh_token = "adapter-rotated-private-refresh", token_type = "Bearer", expires_in = 3600 });
            }
            Assert.Equal("runtime-private-access", outgoing.Headers.Authorization!.Parameter);
            Assert.Equal(purpose == CredentialPurpose.EmailAccount ? "gmail.googleapis.com" : "www.googleapis.com", outgoing.RequestUri!.Host);
            return purpose == CredentialPurpose.EmailAccount ? OAuthJson(new { emailAddress = "fixture@example.test" }) : OAuthJson(new { user = new { emailAddress = "fixture@example.test" } });
        });
        var service = RuntimeChecks(f, OAuthProvider(f, http, false, SharedOAuth()));
        var first = await service.ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, first.Outcome); Assert.Equal(2, calls);
        Assert.Equal(first, await service.ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim.Generation, request.Operation.ActionHash));
        Assert.Equal(2, calls); Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId));
        var value = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
        Assert.Contains("adapter-rotated-private-refresh", f.Protector.Unprotect(value.CredentialId, value.ProtectionVersion, value.ProtectedPayload));
        Assert.DoesNotContain("runtime-private-access", JsonSerializer.Serialize(first));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Concurrent_exact_credential_check_callers_share_only_the_saved_receipt_and_never_provider_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var (setup, request, run) = await ApprovedRuntimeCheck(f);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifier = new RuntimeCheckVerifier(async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return new(); });
        var service = RuntimeChecks(f, verifier); var reference = new TenantRunReference(setup.Tenant, run.AgentRunId);
        var first = service.ExecuteAsync(reference, request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(OperationGrantStatus.Claimed, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId))!.Status);
            Assert.Equal(AgentRunSideEffectDisposition.InFlight, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!.SideEffect.Disposition);
            for (var i = 0; i < 3; i++)
            {
                var duplicate = await Assert.ThrowsAsync<SecurityConflictException>(() => RuntimeChecks(f, verifier).ExecuteAsync(reference,
                    request.RequestId, request.Operation.OperationId, run.Claim.Generation, request.Operation.ActionHash).AsTask());
                Assert.Equal("credential_check_in_progress", duplicate.ReasonCode);
            }
            Assert.Equal(1, verifier.Calls);
        }
        finally { release.TrySetResult(); }
        var receipt = await first.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome); Assert.Equal(1, verifier.Calls);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Uncertain_runtime_check_stops_replay_and_requires_reconnection_without_exposing_provider_errors(bool sqlite, bool reconnect)
    {
        await using var f = await Fixture.Create(sqlite); var (setup, request, run) = await ApprovedRuntimeCheck(f);
        var verifier = new RuntimeCheckVerifier((_, _) => reconnect ? ValueTask.FromResult(new CredentialOAuthCheck(ReconnectRequired: true))
            : ValueTask.FromException<CredentialOAuthCheck>(new IOException("private-provider-body private-refresh")));
        var service = RuntimeChecks(f, verifier);
        var receipt = await service.ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
        Assert.Equal(reconnect ? TenantOperationOutcome.Completed : TenantOperationOutcome.Indeterminate, receipt.Outcome);
        Assert.Equal(reconnect ? "credential_reconnect_required" : "credential_check_uncertain", receipt.ReasonCode);
        var value = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
        Assert.Equal(DriveConnectionStatus.ReconnectRequired, value.DriveAccount!.ConnectionStatus); Assert.Equal("", value.ProtectedPayload);
        Assert.Equal(reconnect ? OperationGrantStatus.Consumed : OperationGrantStatus.Indeterminate,
            (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId))!.Status);
        Assert.Equal(receipt, await service.ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim.Generation, request.Operation.ActionHash));
        Assert.Equal(1, verifier.Calls); Assert.DoesNotContain("private-provider", JsonSerializer.Serialize(receipt));
        Assert.Equal(reconnect ? "allowed" : "indeterminate", Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == TenantCredentialCheckRules.Action).Decision);
    }

    private static async Task<TenantCredentialCheckDispatch> AbandonRuntimeCheck(Fixture f, RuntimeCredentialSetup setup, TenantApprovalRequest request, AgentRun run)
    {
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        var decision = (await ((ITenantApprovedOperationStore)f.Iam).FindApprovalDecisionAsync(setup.Tenant, request.RequestId))!;
        var grant = await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId);
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, run.AgentRunId, run.Claim!.Generation, run.Revision,
            request.Revision, decision.DecisionId, grant?.Revision, request.Operation.ActionHash, TenantOperationOutcome.Completed, "operation_completed");
        var result = await ((ITenantCredentialCheckStore)f.Iam).TryStartCredentialCheckAsync(Next(state), state.Tenant.Revision, commit,
            new(f.Ids.NewId(), setup.Tenant, request.ExecutorPrincipalId, TenantCredentialCheckRules.DispatchAction, request.ResourceId, "allowed", "credential_check_dispatched", f.Clock.GetUtcNow()));
        return Assert.IsType<TenantCredentialCheckDispatch>(result!.Dispatch);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Abandoned_credential_dispatch_recovers_after_lease_or_deleted_session_without_provider_replay(bool sqlite, bool deleted)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T12:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var (setup, request, run) = await ApprovedRuntimeCheck(f);
        var dispatch = await AbandonRuntimeCheck(f, setup, request, run);
        var verifier = new RuntimeCheckVerifier((_, _) => throw new InvalidOperationException("Recovery must never call the provider."));
        Assert.Equal(0, await RuntimeChecks(f, verifier).RecoverAsync());
        if (deleted)
        {
            var current = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!;
            var cancelled = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(current.Owner, current.AgentRunId,
                new AgentRunCommand.RequestCancellation(current.Revision, clock.GetUtcNow(), null), f.Ids.NewId());
            await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(cancelled.Owner, cancelled.AgentRunId,
                new AgentRunCommand.CommitCancellation(cancelled.Revision, clock.GetUtcNow(), run.Claim!.Generation, null), f.Ids.NewId());
            var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, run.SessionId))!;
            await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(run.SessionId, session.Snapshot.Revision, TenantSessionAction.Delete),
                (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
            Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId));
        }
        else clock.Advance(TimeSpan.FromMinutes(6));
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, clock: clock) : f.Iam;
        Assert.Equal(1, await RuntimeChecks(f, verifier, reopened).RecoverAsync()); Assert.Equal(0, await RuntimeChecks(f, verifier, reopened).RecoverAsync());
        var receipt = (await ((ITenantApprovedOperationStore)reopened).FindOperationReceiptAsync(setup.Tenant, request.Operation.OperationId))!;
        Assert.Equal(TenantOperationOutcome.Indeterminate, receipt.Outcome); Assert.Equal("credential_check_uncertain", receipt.ReasonCode);
        Assert.NotNull((await ((ITenantCredentialCheckStore)reopened).FindCredentialCheckDispatchAsync(setup.Tenant, request.Operation.OperationId))!.CompletedAt);
        Assert.Equal(OperationGrantStatus.Indeterminate, (await ((ITenantApprovalStore)reopened).FindOperationGrantAsync(setup.Tenant, request.RequestId))!.Status);
        Assert.Equal(0, verifier.Calls);
        var credential = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
        Assert.Equal(DriveConnectionStatus.ReconnectRequired, credential.DriveAccount!.ConnectionStatus);
        var late = await ((ITenantCredentialCheckStore)reopened).TrySettleCredentialCheckAsync(Next((await reopened.ReadTenantAsync(setup.Tenant))!),
            (await reopened.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, dispatch, new(),
            new(f.Ids.NewId(), setup.Tenant, request.ExecutorPrincipalId, TenantCredentialCheckRules.Action, request.ResourceId, "allowed", "credential_connection_checked", clock.GetUtcNow()));
        Assert.Equal(receipt, late);
    }

    [Theory]
    [InlineData(false, "read")] [InlineData(true, "read")]
    [InlineData(false, "use")] [InlineData(true, "use")]
    [InlineData(false, "witness")] [InlineData(true, "witness")]
    [InlineData(false, "host")] [InlineData(true, "host")]
    public async Task Current_credential_check_boundaries_record_denial_without_dispatch_or_secret_consumption(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var (setup, request, run) = await ApprovedRuntimeCheck(f, true);
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        if (boundary is "read" or "use")
        {
            var role = state.Roles.Single(r => r.Name == "Bound credential check");
            await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, role.RoleId, role.Name,
                boundary == "read" ? ["credentials.use"] : ["credentials.read"], state.Tenant.Revision);
        }
        else if (boundary == "witness")
        {
            var value = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
            Assert.True(await ((ITenantCredentialOAuthStore)f.Iam).TryCheckCredentialOAuthAsync(Next(state), state.Tenant.Revision, setup.CredentialId,
                new(setup.Owner.AccountId, setup.Owner.AuthenticationSessionId, setup.Owner.PrincipalId), value.Revision, value.SecretRevision,
                new(f.Protector.Protect(value.CredentialId, value.ProtectionVersion, GoogleOAuthRefreshGrant.Write("newer-private-refresh", "tenant-client"))),
                f.Audit(setup.Tenant, setup.Owner.PrincipalId, setup.CredentialId)));
        }
        var retained = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
        var verifier = new RuntimeCheckVerifier((_, _) => throw new InvalidOperationException("Denied check cannot use a secret.")) { HostEnabled = boundary != "host" };
        var receipt = await RuntimeChecks(f, verifier).ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Denied, receipt.Outcome);
        Assert.Equal(boundary switch { "read" => "executor_read_required", "use" => "operation_authority_changed", "witness" => "operation_witness_changed", _ => "oauth_host_disabled" }, receipt.ReasonCode);
        Assert.Equal(0, verifier.Calls); Assert.Null(await ((ITenantCredentialCheckStore)f.Iam).FindCredentialCheckDispatchAsync(setup.Tenant, request.Operation.OperationId));
        var after = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
        Assert.Equal(retained.SecretRevision, after.SecretRevision); Assert.True(retained.ProtectedPayload == after.ProtectedPayload);
        Assert.Equal(AgentRunSideEffectDisposition.DefinitelyFailed, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!.SideEffect.Disposition);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task A_newer_credential_grant_wins_over_a_late_provider_rotation_without_being_overwritten(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var (setup, request, run) = await ApprovedRuntimeCheck(f);
        string? newer = null;
        var verifier = new RuntimeCheckVerifier(async (original, _) => {
            var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
            newer = f.Protector.Protect(original.CredentialId, original.ProtectionVersion, GoogleOAuthRefreshGrant.Write("newer-private-refresh", "tenant-client"));
            Assert.True(await ((ITenantCredentialOAuthStore)f.Iam).TryCheckCredentialOAuthAsync(Next(state), state.Tenant.Revision, setup.CredentialId,
                new(setup.Owner.AccountId, setup.Owner.AuthenticationSessionId, setup.Owner.PrincipalId), original.Revision, original.SecretRevision,
                new(newer), f.Audit(setup.Tenant, setup.Owner.PrincipalId, setup.CredentialId)));
            return new(f.Protector.Protect(original.CredentialId, original.ProtectionVersion, GoogleOAuthRefreshGrant.Write("obsolete-private-refresh", "tenant-client")));
        });
        var receipt = await RuntimeChecks(f, verifier).ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Indeterminate, receipt.Outcome); Assert.Equal("credential_check_target_changed", receipt.ReasonCode);
        var value = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
        Assert.True(value.ProtectedPayload == newer); Assert.Equal(DriveConnectionStatus.Connected, value.DriveAccount!.ConnectionStatus); Assert.Equal(1, verifier.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Timed_out_noncooperative_provider_cannot_restore_or_repeat_an_uncertain_credential_check(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T12:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var (setup, request, run) = await ApprovedRuntimeCheck(f);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<CredentialOAuthCheck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifier = new RuntimeCheckVerifier((_, _) => { entered.TrySetResult(); return new(late.Task); });
        var service = RuntimeChecks(f, verifier);
        var checking = service.ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); clock.Advance(TimeSpan.FromSeconds(31));
        var receipt = await checking.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(TenantOperationOutcome.Indeterminate, receipt.Outcome);
        var before = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
        late.TrySetResult(new(f.Protector.Protect(before.CredentialId, before.ProtectionVersion, GoogleOAuthRefreshGrant.Write("too-late-private-refresh", "tenant-client"))));
        await late.Task;
        Assert.Equal(receipt, await service.ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim.Generation, request.Operation.ActionHash));
        var after = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
        Assert.Equal(before.SecretRevision, after.SecretRevision); Assert.Equal("", after.ProtectedPayload); Assert.Equal(1, verifier.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task An_independent_operation_cannot_refresh_the_credential_while_a_durable_dispatch_owns_it(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var (setup, first, firstRun) = await ApprovedRuntimeCheck(f);
        await AbandonRuntimeCheck(f, setup, first, firstRun);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var session = await f.Sessions.CreateAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner, SessionMode.Text, await Revision(), setup.Agent.Instance.Revision);
        var admitted = await f.RunService().SubmitTextAsync(setup.Tenant, session.Owner.SessionId, setup.Owner, f.Ids.NewId(), "Independent connection check", await Revision(), 1);
        async Task<AgentRun> Claim()
        {
            var queued = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, admitted.RunId))!; var at = f.Clock.GetUtcNow();
            return await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
                new AgentRunCommand.Claim(queued.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        }
        var run = await Claim(); var approvals = RuntimeApprovals(f);
        var request = (await approvals.PrepareCredentialCheckAsync(new(setup.Tenant, run.AgentRunId), "work-drive", run.Revision, run.Claim!.Generation)).Request!;
        Assert.NotEqual(first.RootActivationId, request.RootActivationId);
        await approvals.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true, request.Operation.ActionHash, await Revision(), request.Revision);
        Assert.Equal(1, await approvals.DispatchPendingAsync()); run = await Claim();
        var verifier = new RuntimeCheckVerifier((_, _) => throw new InvalidOperationException("A reserved credential cannot be used twice."));
        var receipt = await RuntimeChecks(f, verifier).ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Denied, receipt.Outcome); Assert.Equal("credential_check_in_progress", receipt.ReasonCode); Assert.Equal(0, verifier.Calls);
        Assert.Equal(OperationGrantStatus.Revoked, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId))!.Status);
        Assert.Null(await ((ITenantCredentialCheckStore)f.Iam).FindCredentialCheckDispatchAsync(setup.Tenant, request.Operation.OperationId));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Known_provider_completion_keeps_a_cancelled_Run_terminal_while_safely_retaining_the_rotated_grant(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var (setup, request, run) = await ApprovedRuntimeCheck(f);
        var verifier = new RuntimeCheckVerifier(async (original, _) => {
            var current = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!;
            var cancelled = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(current.Owner, current.AgentRunId,
                new AgentRunCommand.RequestCancellation(current.Revision, f.Clock.GetUtcNow(), null), f.Ids.NewId());
            await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(cancelled.Owner, cancelled.AgentRunId,
                new AgentRunCommand.CommitCancellation(cancelled.Revision, f.Clock.GetUtcNow(), run.Claim!.Generation, null), f.Ids.NewId());
            return new(f.Protector.Protect(original.CredentialId, original.ProtectionVersion, GoogleOAuthRefreshGrant.Write("completed-private-refresh", "tenant-client")));
        });
        var receipt = await RuntimeChecks(f, verifier).ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome); Assert.Equal(1, verifier.Calls);
        var terminal = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Cancelled, terminal.Status); Assert.Null(terminal.Claim);
        var value = (await f.Credentials.FindTenantCredentialAsync(setup.Tenant, setup.CredentialId))!.Value;
        Assert.Contains("completed-private-refresh", f.Protector.Unprotect(value.CredentialId, value.ProtectionVersion, value.ProtectedPayload));
        Assert.Equal(OperationGrantStatus.Consumed, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId))!.Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Factual_completion_after_executor_role_revocation_retains_no_future_credential_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var (setup, request, run) = await ApprovedRuntimeCheck(f);
        var verifier = new RuntimeCheckVerifier(async (original, _) => {
            var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
            var assignment = state.Assignments.Single(a => a.PrincipalId == setup.Agent.PrincipalId);
            await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, assignment.AssignmentId, state.Tenant.Revision);
            return new(f.Protector.Protect(original.CredentialId, original.ProtectionVersion, GoogleOAuthRefreshGrant.Write("completed-private-refresh", "tenant-client")));
        });
        var receipt = await RuntimeChecks(f, verifier).ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        Assert.DoesNotContain(state.Assignments, a => a.PrincipalId == setup.Agent.PrincipalId);
        foreach (var permission in new[] { "credentials.read", "credentials.use" })
            Assert.False((await f.Authorizer.CheckTenantAsync(state, setup.Agent.PrincipalId, PermissionCatalog.Get(permission),
                AccessScope.ForResource(setup.Tenant, "credential", setup.CredentialId.ToString("D")), default)).Allowed);
        Assert.Equal(OperationGrantStatus.Consumed, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId))!.Status);
        Assert.Equal(1, verifier.Calls);
    }

    [Fact]
    public async Task Credential_dispatch_schema_binds_actual_lineage_and_refuses_a_lossy_completed_fence_downgrade()
    {
        await using var f = await Fixture.Create(true); var (setup, request, run) = await ApprovedRuntimeCheck(f);
        var service = RuntimeChecks(f, new RuntimeCheckVerifier((_, _) => ValueTask.FromResult(new CredentialOAuthCheck())));
        var receipt = await service.ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
        await using (var db = await f.Contexts!.CreateDbContextAsync())
        {
            var foreign = Guid.NewGuid().ToString("D"); var badHash = new string('0', 64);
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantCredentialCheckDispatches SET CredentialId={foreign} WHERE OperationId={request.Operation.OperationId}"));
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantCredentialCheckDispatches SET RunId={foreign} WHERE OperationId={request.Operation.OperationId}"));
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantCredentialCheckDispatches SET ActionHash={badHash} WHERE OperationId={request.Operation.OperationId}"));
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261017090143_TenantCredentialApprovalTargets"));
        }
        Assert.Equal(receipt, await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(setup.Tenant, request.Operation.OperationId));
        Assert.NotNull((await ((ITenantCredentialCheckStore)f.Iam).FindCredentialCheckDispatchAsync(setup.Tenant, request.Operation.OperationId))!.CompletedAt);
    }

    private sealed class DelayedDispatchReturn(ITenantCredentialCheckStore inner, FakeTimeProvider clock) : ITenantCredentialCheckStore
    {
        public ValueTask<TenantCredentialCheckDispatch?> FindCredentialCheckDispatchAsync(Guid tenantId, Guid operationId, CancellationToken ct = default) => inner.FindCredentialCheckDispatchAsync(tenantId, operationId, ct);
        public ValueTask<IReadOnlyList<TenantCredentialCheckDispatch>> RecoverableCredentialChecksAsync(DateTimeOffset at, int limit, CancellationToken ct = default) => inner.RecoverableCredentialChecksAsync(at, limit, ct);
        public async ValueTask<TenantCredentialCheckStart?> TryStartCredentialCheckAsync(TenantSecurityState next, long revision, TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
        { var result = await inner.TryStartCredentialCheckAsync(next, revision, commit, audit, ct); clock.Advance(TimeSpan.FromMinutes(6)); return result; }
        public ValueTask<TenantOperationReceipt?> TrySettleCredentialCheckAsync(TenantSecurityState next, long revision, TenantCredentialCheckDispatch dispatch,
            CredentialOAuthCheck? check, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default) => inner.TrySettleCredentialCheckAsync(next, revision, dispatch, check, audit, recovery, ct);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Expiry_during_dispatch_commit_never_starts_provider_I_O_with_an_expired_claim(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T12:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var (setup, request, run) = await ApprovedRuntimeCheck(f);
        var verifier = new RuntimeCheckVerifier((_, _) => ValueTask.FromResult(new CredentialOAuthCheck()));
        var service = RuntimeChecks(f, verifier, checkStore: new DelayedDispatchReturn((ITenantCredentialCheckStore)f.Iam, clock));
        var receipt = await service.ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Indeterminate, receipt.Outcome); Assert.Equal(0, verifier.Calls);
        Assert.Equal(OperationGrantStatus.Indeterminate, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId))!.Status);
    }
}
