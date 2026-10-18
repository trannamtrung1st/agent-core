using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory]
    [InlineData(false, "unrelated")] [InlineData(true, "unrelated")]
    [InlineData(false, "restrict")] [InlineData(true, "restrict")]
    [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "cancel-read")] [InlineData(true, "cancel-read")]
    public async Task Current_policy_read_restarts_after_one_revision_change_but_never_recovers_revoked_requester(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var requester = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Read and chat", ["agents.chat", "sessions.read"], await Revision());
        var grant = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, requester.PrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, requester, f.Ids.NewId(), "Read current policy coherently", await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam; var storage = (ITenantRuntimeStore)f.Iam;
        var run = (await runs.FindTenantRunAsync(setup.Tenant, receipt.RunId))!;
        run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var reads = 0; using var cancellation = new CancellationTokenSource();
        var hook = new CurrentPolicyBuiltinHook(f.BuiltIns, async () => {
            if (++reads != 1) return;
            if (boundary == "revoke") await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, grant.AssignmentId, await Revision());
            else if (boundary == "restrict")
            {
                var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
                var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
                Assert.NotNull(await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(next, state.Tenant.Revision,
                    new(setup.Agent.Instance.InstanceId, setup.Agent.Instance.Revision, AutomationPolicyChanges: [], SetSettingsOverrides: true,
                        SettingsOverrides: new(HomeAccess: new("Disabled")),
                        Initiator: new(setup.Owner.AccountId, setup.Owner.AuthenticationSessionId, setup.Owner.PrincipalId)), f.Clock.GetUtcNow(),
                    new(f.Ids.NewId(), setup.Tenant, setup.Owner.PrincipalId, "agents.settings.update", setup.Agent.Instance.InstanceId.ToString("D"), "allowed", "settings_changed", f.Clock.GetUtcNow())));
            }
            else await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Unrelated role during policy read", ["agents.read"], await Revision());
            if (boundary == "cancel-read") cancellation.Cancel();
        });
        var authority = new TenantRunExecutionAuthority(new(f.Iam, runs, (ITenantAgentStore)f.Iam, f.Authorizer,
            new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, f.Clock)), f.Iam, f.Clock, f.RunConfiguration(hook));
        if (boundary == "cancel-read")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authority.CurrentDefinitionAsync(run, cancellation.Token).AsTask());
            Assert.Equal(1, reads);
        }
        else
        {
            var policy = await authority.CurrentDefinitionAsync(run);
            if (boundary == "revoke") { Assert.Null(policy); Assert.Equal(1, reads); }
            else { Assert.NotNull(policy); Assert.Equal(2, reads); }
            if (boundary == "restrict") Assert.Equal(HomeAccess.Disabled, policy!.StateIsolationPolicy!.HomeAccess);
        }
        // Only the read is restarted. The Run, claim and input graph are untouched.
        var saved = (await runs.FindTenantRunAsync(setup.Tenant, run.AgentRunId))!;
        Assert.Equal(run.Revision, saved.Revision); Assert.Equal(run.Status, saved.Status);
        Assert.Equal(run.Claim!.Generation, saved.Claim!.Generation);
        Assert.Equal(run.Claim.LeaseExpiresAtUtc, saved.Claim.LeaseExpiresAtUtc);
        Assert.Equal(run.Admission.ExecutionContext, saved.Admission.ExecutionContext);
        Assert.Equal(run.Admission.Configuration!.Definition.AgentInstructions, saved.Admission.Configuration!.Definition.AgentInstructions);
        Assert.Equal(run.Admission.Configuration.Definition.StateIsolationPolicy?.HomeAccess, saved.Admission.Configuration.Definition.StateIsolationPolicy?.HomeAccess);
        Assert.Single(await runs.ListTenantRunIdsAsync(setup.Tenant, setup.Session));
    }
}
