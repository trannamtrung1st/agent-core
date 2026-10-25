using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantPreferenceOperationTests
{
    private const string Preferences = """{"set":{"language":"vi"},"clear":[]}""";

    [Theory]
    [InlineData(false, "read")] [InlineData(true, "read")]
    [InlineData(false, "update")] [InlineData(true, "update")]
    [InlineData(false, "review")] [InlineData(true, "review")]
    [InlineData(false, "requester")] [InlineData(true, "requester")]
    [InlineData(false, "assisted")] [InlineData(true, "assisted")]
    [InlineData(false, "autoApply")] [InlineData(true, "autoApply")]
    public async Task Direct_preferences_require_executor_permissions_and_current_Allowed_Managed_auto_apply(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite);
        var setup = await Setup(f, permissions: boundary != "read", update: boundary != "update",
            mode: boundary == "assisted" ? HarnessManagementMode.Assisted : HarnessManagementMode.Managed, autoApply: boundary != "autoApply", restrictedRequester: boundary == "requester");
        if (boundary == "review")
            await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
                .SaveAsync(setup.Tenant, setup.Owner, "agents.update", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 0);
        var result = await Service(f).ExecuteAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Run.Claim!.Generation, "no-effect", Preferences);
        Assert.Equal(boundary is "update" or "review" or "requester" ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Denied, result.Decision);
        Assert.Null(result.Receipt); await NoEffect(f, setup);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Strict_preference_contract_rejects_authority_fields_and_unrelated_settings_without_writing(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        foreach (var json in new[] {
            """{"set":{"language":"vi"},"clear":[],"tenantId":"forged"}""",
            """{"set":{"maxOutputTokens":99999},"clear":[]}""",
            """{"set":{"triggerPolicy":{}},"clear":[]}""",
            """{"set":{"language":"vi","language":"en"},"clear":[]}""",
            """{"set":{"language":"vi"},"clear":["language"]}""",
            """{"set":{"responseLength":"unbounded"},"clear":[]}""",
            """{"set":{"askOneQuestionAtATime":"true"},"clear":[]}""",
            """{"set":{},"clear":[]}""" })
            await Assert.ThrowsAsync<ArgumentException>(() => Service(f).ExecuteAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Run.Claim!.Generation, "invalid-call", json).AsTask());
        await NoEffect(f, setup);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Exact_call_replay_rejects_changed_arguments_and_clear_restores_immutable_defaults(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var service = Service(f);
        var reference = new TenantRunReference(setup.Tenant, setup.Run.AgentRunId); var generation = setup.Run.Claim!.Generation;
        await service.ExecuteAsync(reference, generation, "same-call", """{"set":{"language":"vi","askOneQuestionAtATime":false},"clear":[]}""");
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.ExecuteAsync(reference, generation, "same-call", """{"set":{"language":"en"},"clear":[]}""").AsTask());
        var cleared = await service.ExecuteAsync(reference, generation, "clear-call", """{"set":{},"clear":["language"]}""");
        Assert.Equal(4, cleared.Receipt!.InstanceRevision);
        var current = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(setup.Tenant, setup.Agent.Instance.InstanceId))!.Instance;
        Assert.Null(current.SettingsOverrides!.Language); Assert.False(current.SettingsOverrides.AskOneQuestionAtATime!.Value);
        var (_, baseline) = await f.RunConfiguration().BaselineAsync(setup.Tenant, current.InstanceId, default);
        Assert.Equal(baseline.ConversationPolicy.Language, (await f.RunConfiguration().ResolveAsync(setup.Tenant, current.InstanceId, default)).Configuration.Configuration.Definition.ConversationPolicy.Language);
        Assert.Equal(2, (await f.Iam.ReadAuditAsync(setup.Tenant, 200)).Count(a => a.Action == "operations.preferences.apply"));
    }

    [Theory]
    [InlineData(false, "account")] [InlineData(true, "account")]
    [InlineData(false, "role")] [InlineData(true, "role")]
    [InlineData(false, "lease")] [InlineData(true, "lease")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "runCancel")] [InlineData(true, "runCancel")]
    [InlineData(false, "freeze")] [InlineData(true, "freeze")]
    [InlineData(false, "compete")] [InlineData(true, "compete")]
    public async Task Direct_commit_rechecks_account_revision_live_claim_and_cancellation_and_has_one_race_winner(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f);
        if (boundary == "account")
        {
            var other = await f.Identity.RegisterAsync("other-owner@example.test", "Other owner", "strong-other-password");
            var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
            var invitation = await f.Access.InviteAsync(setup.Owner.PrincipalId, setup.Tenant, other.LoginKey,
                state.Roles.Single(r => r.IsBuiltin && r.Key == BuiltinAccessRoles.Owner).RoleId, state.Tenant.Revision);
            await f.Access.AcceptInvitationAsync(other.AccountId, setup.Tenant, invitation.Invitation.InvitationId, invitation.Token);
        }
        var gate = new PreferenceGate((ITenantPreferenceOperationStore)f.Iam); using var cancellation = new CancellationTokenSource();
        var reference = new TenantRunReference(setup.Tenant, setup.Run.AgentRunId);
        var pending = Service(f, effects: gate).ExecuteAsync(reference, setup.Run.Claim!.Generation, "race-call", Preferences, cancellation.Token).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        TenantOperationReceipt? winner = null;
        try
        {
            if (boundary == "account")
            {
                var account = (await f.Iam.ReadAccountAsync(setup.Owner.AccountId))!;
                var actor = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == setup.Owner.AccountId);
                Assert.True(await f.Iam.TryUpdateAccountAsync(account with { Status = IdentityStatus.Suspended, SecurityStamp = f.Ids.NewId().ToString("N") }, account.SecurityStamp,
                    new(f.Ids.NewId(), null, actor.PrincipalId, "system.accounts.manage", account.AccountId.ToString("D"), "allowed", "account_suspended", clock.GetUtcNow())));
            }
            if (boundary == "role")
            {
                var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
                await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, state.Assignments.Single(a => a.PrincipalId == setup.Agent.PrincipalId).AssignmentId, state.Tenant.Revision);
            }
            if (boundary == "lease") clock.Advance(TimeSpan.FromMinutes(5));
            if (boundary == "cancel") cancellation.Cancel();
            if (boundary == "freeze") await ChangeManagement(f, setup, setup.Agent.Instance.HarnessManagement!.Policy with { Frozen = true });
            if (boundary == "runCancel")
                await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(setup.Run.Owner, setup.Run.AgentRunId,
                    new AgentRunCommand.RequestCancellation(setup.Run.Revision, clock.GetUtcNow(), null), f.Ids.NewId());
            if (boundary == "compete") winner = (await Service(f).ExecuteAsync(reference, setup.Run.Claim.Generation, "race-call", Preferences)).Receipt;
        }
        finally { gate.Proceed.TrySetResult(); }
        if (winner is not null)
        {
            Assert.Equal(winner, (await pending).Receipt);
            Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "operations.preferences.apply");
        }
        else
        {
            var error = await Record.ExceptionAsync(async () => await pending);
            Assert.True(error is AccessDeniedException or SecurityConflictException or OperationCanceledException, error?.ToString());
            await NoEffect(f, setup, boundary == "freeze" ? 3 : 2);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Shared_executor_requires_actual_owned_context_generation_and_a_configured_tenant_gate(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var gate = new TenantToolAuthorization((ITenantRunStore)f.Iam,
            new(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, f.Authorizer, new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, f.Clock)), f.Clock, preferences: Service(f));
        var tools = new SessionToolExecutor(tenantAuthorization: gate);
        var frame = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: setup.Run.AgentInstanceId, AgentRunId: setup.Run.AgentRunId,
            OwnedSessionId: setup.Run.SessionId, TenantId: setup.Tenant, AgentRunGeneration: setup.Run.Claim!.Generation);
        var call = new ModelToolCall("gate-call", TenantPreferenceToolContract.Name, Preferences);
        foreach (var forged in new ToolExecutionAdmission?[] { null, frame with { TenantId = f.Ids.NewId() }, frame with { AgentRunId = f.Ids.NewId() },
            frame with { OwnedSessionId = f.Ids.NewId() }, frame with { AgentRunGeneration = f.Ids.NewId() } })
        {
            Assert.Equal(ToolPolicyDecision.Deny, await gate.PolicyAsync(setup.Run.SessionId, call, forged, default));
            Assert.Contains("forbidden", (await tools.ExecuteAsync(setup.Run.Admission.Configuration!.Definition, setup.Run.SessionId, call, 4000, admission: forged)).Text);
        }
        Assert.Contains("forbidden", (await new SessionToolExecutor().ExecuteAsync(setup.Run.Admission.Configuration!.Definition, setup.Run.SessionId, call, 4000, admission: frame)).Text);
        await NoEffect(f, setup);
    }

    [Theory]
    [InlineData(false, "liveFrozen")] [InlineData(true, "liveFrozen")]
    [InlineData(false, "liveAssisted")] [InlineData(true, "liveAssisted")]
    [InlineData(false, "pinnedUnselected")] [InlineData(true, "pinnedUnselected")]
    public async Task Pinned_and_current_management_both_gate_direct_preferences(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f, autoApply: boundary != "pinnedUnselected");
        await ChangeManagement(f, setup, setup.Agent.Instance.HarnessManagement!.Policy with {
            Frozen = boundary == "liveFrozen", Mode = boundary == "liveAssisted" ? HarnessManagementMode.Assisted : HarnessManagementMode.Managed,
            ManagedAutoApply = [ManagementAutoApply.ConversationPreferences] });
        var result = await Service(f).ExecuteAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Run.Claim!.Generation, "no-effect", Preferences);
        Assert.Equal(OperationDecisionKind.Denied, result.Decision); Assert.Equal("management_unavailable", result.ReasonCode);
        await NoEffect(f, setup, 3);
    }

    private static async Task ChangeManagement(Fixture f, Environment setup, HarnessManagementPolicy policy)
    {
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!; var at = f.Clock.GetUtcNow();
        Assert.NotNull(await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
            new(setup.Agent.Instance.InstanceId, 2, HarnessManagement: new(policy, 3, Overrides: new(Mode: new(policy.Mode), Scopes: new(policy.Scopes.ToArray()), AutoApply: new((policy.ManagedAutoApply ?? []).ToArray())))), at,
            new(f.Ids.NewId(), setup.Tenant, setup.Owner.PrincipalId, "agents.self-management.update", setup.Agent.Instance.InstanceId.ToString("D"), "allowed", "configuration_updated", at)));
    }

    private static async Task NoEffect(Fixture f, Environment setup, long revision = 2)
    {
        Assert.Equal(revision, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(setup.Tenant, setup.Agent.Instance.InstanceId))!.Instance.Revision);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "operations.preferences.apply");
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(setup.Tenant, null, null, new(0, 100))).Items);
        Assert.Null(await ((ITenantPreferenceOperationStore)f.Iam).FindDirectOperationReceiptAsync(setup.Tenant, setup.Run.AgentRunId, "race-call"));
    }
    private sealed class PreferenceGate(ITenantPreferenceOperationStore inner) : ITenantPreferenceOperationStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TenantOperationReceipt?> FindDirectOperationReceiptAsync(Guid tenant, Guid run, string call, CancellationToken ct = default) => inner.FindDirectOperationReceiptAsync(tenant, run, call, ct);
        public async ValueTask<TenantOperationReceipt?> TryCommitPreferencesAsync(TenantSecurityState next, long expected, TenantPreferenceCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
        { Entered.TrySetResult(); await Proceed.Task.WaitAsync(ct); return await inner.TryCommitPreferencesAsync(next, expected, commit, audit, ct); }
    }
}
