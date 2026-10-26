using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_instance_sparse_settings_and_policies_preserve_inheritance_and_saved_snapshots(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Sparse owned settings", 1);
        var p1 = await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), 1);
        var agent = await agents.CreateFromPublicationAsync(owner.OwnerPrincipalId, owner.TenantId, Guid.Parse(draft.DefinitionId), 1, await Revision()); var id = agent.Instance.InstanceId;
        var changed = await configuration.PatchSettingsAsync(owner.OwnerPrincipalId, owner.TenantId, id, "instructions", await Revision(), 1,
            Fields(new { agentInstructions = "Local owned instructions." }), []);
        Assert.Equal("Local owned instructions.", (await configuration.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Definition.AgentInstructions);
        Assert.Equal(p1.Payload.AgentInstructions, (await f.Definitions.FindDefinitionPublicationAsync(owner.TenantId, Guid.Parse(draft.DefinitionId), 1))!.Payload.AgentInstructions);
        var read = await configuration.ReadSettingsAsync(owner.OwnerPrincipalId, owner.TenantId, id);
        Assert.Equal("instance", read.Items.Single(s => s.Section == "instructions").Sources["agentInstructions"]);
        var budget = new ExecutionBudgetPolicy(Standard: new(8, 60, 1, SandboxCommandSeconds: 5));
        changed = await configuration.UpdateBudgetsAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), changed.Instance.Revision, budget);
        Assert.Equal("instance", (await configuration.ReadBudgetsAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Effective[ExecutionBudgetClass.Standard].Source);
        changed = await configuration.UpdateUnattendedModelAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), changed.Instance.Revision, AgentCore.Domain.Models.ModelPreference.Fixed(AgentCore.Infrastructure.Providers.Synthetic.SyntheticModelCatalog.Create().DefaultOfferingId, "low"));
        var scopes = new[] { HarnessManagementScope.Settings, HarnessManagementScope.Skills };
        var overrides = new ManagementPolicyOverrides(new(HarnessManagementMode.Assisted), new(scopes), ConfigurationAssistance: new(false));
        changed = await configuration.UpdateManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), changed.Instance.Revision, overrides);
        scopes[0] = HarnessManagementScope.Connections;
        var management = await configuration.ReadManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id);
        Assert.Contains(HarnessManagementScope.Settings, management.Policy.Effective.Scopes);
        Assert.DoesNotContain(HarnessManagementScope.Connections, management.Policy.Effective.Scopes);
        ((IList<HarnessManagementScope>)management.Policy.Effective.Scopes)[0] = HarnessManagementScope.Connections;
        Assert.Contains(HarnessManagementScope.Settings, (await configuration.ReadManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Policy.Effective.Scopes);
        changed = await configuration.FreezeManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), changed.Instance.Revision);
        management = await configuration.ReadManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id);
        Assert.True(management.Policy.Effective.Frozen); Assert.False(management.Policy.Effective.BuiltInSkills!.ConfigurationAssistance);
        Assert.Equal(HarnessManagementMode.Assisted, management.Policy.Effective.Mode);
        Assert.Contains(HarnessManagementScope.Settings, management.Policy.Effective.Scopes);
        if (sqlite)
        {
            var reopened = ConfigurationServices(f, new SqliteIamStore(f.Contexts!, f.Content)).Configuration;
            Assert.Equal("Local owned instructions.", (await reopened.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Definition.AgentInstructions);
            Assert.True((await reopened.ReadManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Policy.Effective.Frozen);
            Assert.Equal(budget, (await reopened.ReadBudgetsAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Overrides);
            Assert.Equal(AgentCore.Infrastructure.Providers.Synthetic.SyntheticModelCatalog.Create().DefaultOfferingId, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, id))!.Instance.UnattendedModel?.OfferingId);
        }
        changed = await configuration.PatchSettingsAsync(owner.OwnerPrincipalId, owner.TenantId, id, "instructions", await Revision(), changed.Instance.Revision, Fields(new { }), ["agentInstructions"]);
        Assert.Equal(p1.Payload.AgentInstructions, (await configuration.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Definition.AgentInstructions);
        changed = await configuration.UpdateBudgetsAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), changed.Instance.Revision, null);
        Assert.NotEqual("instance", (await configuration.ReadBudgetsAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Effective[ExecutionBudgetClass.Standard].Source);
        changed = await configuration.UpdateUnattendedModelAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), changed.Instance.Revision, null);
        Assert.Null(changed.Instance.UnattendedModel?.OfferingId);
        changed = await configuration.UpdateManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), changed.Instance.Revision, new(), false);
        Assert.Equal("definition", (await configuration.ReadManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Policy.Source);
        Assert.False((await configuration.ReadManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Policy.Effective.Frozen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_agent_cannot_enable_or_unfreeze_management_but_can_stop_with_current_own_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, 1); var id = agent.Instance.InstanceId;
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var assignment = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, state.Roles.Single(r => r.Key == "contributor").RoleId,
            AccessScope.ForResource(owner.TenantId, "agent", id.ToString("D")), state.Tenant.Revision);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var policy = new ManagementPolicyOverrides(new(HarnessManagementMode.Assisted), new([HarnessManagementScope.Skills]));
        var revision = await Revision();
        await Assert.ThrowsAsync<AgentCoreException>(() => configuration.UpdateManagementAsync(agent.PrincipalId, owner.TenantId, id, revision, 1, policy).AsTask());
        var changed = await configuration.UpdateManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id, revision, 1, policy);
        revision = await Revision();
        await Assert.ThrowsAsync<AgentCoreException>(() => configuration.UpdateManagementAsync(agent.PrincipalId, owner.TenantId, id, revision, changed.Instance.Revision, policy, false).AsTask());
        changed = await configuration.FreezeManagementAsync(agent.PrincipalId, owner.TenantId, id, revision, changed.Instance.Revision);
        Assert.True((await configuration.ReadManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Policy.Effective.Frozen);
        revision = await Revision();
        await Assert.ThrowsAsync<AgentCoreException>(() => configuration.UpdateManagementAsync(agent.PrincipalId, owner.TenantId, id, revision, changed.Instance.Revision, policy, false).AsTask());
        await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, assignment.AssignmentId, revision);
        revision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => configuration.UpdateBudgetsAsync(agent.PrincipalId, owner.TenantId, id, revision, changed.Instance.Revision, null).AsTask());
        Assert.Equal(changed.Instance.Revision, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, id))!.Instance.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_settings_mutation_rejects_mixed_commands_and_stale_plan_without_partial_state(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, 1); var id = agent.Instance.InstanceId;
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!; var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        var now = DateTimeOffset.UtcNow; var audit = new AuthorizationAudit(f.Ids.NewId(), owner.TenantId, owner.OwnerPrincipalId, "agents.settings.update", id.ToString("D"), "allowed", "test", now);
        var store = (ITenantAgentStore)f.Iam; var count = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var settings = new InstanceSettingsOverrides(AgentInstructions: new("Must roll back"));
        var mixed = new TenantAgentConfigurationChange(id, 1, SetSettingsOverrides: true, SettingsOverrides: settings, SetExecutionBudgets: true, AutomationPolicyChanges: []);
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryChangeTenantAgentConfigurationAsync(next, state.Tenant.Revision, mixed, now, audit).AsTask());
        var stale = mixed with { SetExecutionBudgets = false, AutomationPolicyChanges = [new(Guid.NewGuid(), 1, AgentCore.Domain.Triggers.AutomationStatus.Active, null, null)] };
        await Assert.ThrowsAsync<AgentCoreException>(() => store.TryChangeTenantAgentConfigurationAsync(next, state.Tenant.Revision, stale, now, audit).AsTask());
        Assert.Null((await store.FindTenantAgentAsync(owner.TenantId, id))!.Instance.SettingsOverrides);
        Assert.Equal(1, (await store.FindTenantAgentAsync(owner.TenantId, id))!.Instance.Revision);
        Assert.Equal(state.Tenant.Revision, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        Assert.Equal(count, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        ITriggerStore triggers = f.Contexts is not null ? new SqliteTriggerStore(f.Contexts) : (((InMemoryAgentInstanceStore)f.Instances).TriggerStore ??= new InMemoryTriggerStore());
        var triggerOwner = new TriggerOwner(id, Guid.NewGuid());
        await f.SessionStore.SaveProfileAsync(new UserProfile(triggerOwner.ProfileId, 1, new Dictionary<string, UserProfileValue>(), now), 0);
        var automation = await triggers.CreateAsync(new Automation(Guid.NewGuid(), triggerOwner, AutomationStatus.Active, "Review inventory",
            new FixedIntervalSchedule(60, now), now.AddMinutes(1), null, 0, 1, 1,
            new TriggerProvenance(TriggerAuthorizationOrigin.AdminOwner, null, null, now, now), null));
        var changed = await configuration.PatchSettingsAsync(owner.OwnerPrincipalId, owner.TenantId, id, "triggerPolicy", state.Tenant.Revision, 1, Fields(new { enabled = false }), []);
        Assert.False(changed.Instance.SettingsOverrides!.TriggerEnabled!.Value);
        // Tenant reconciliation cannot mutate an unowned native registration, even when its Instance id matches.
        // Actual owned suspension/restoration is qualified by the canonical HTTP/model Automation cases.
        var retained = (await triggers.GetAsync(triggerOwner, automation.AutomationId))!;
        Assert.Equal(AutomationStatus.Active, retained.Status); Assert.Equal(automation.Revision, retained.Revision);
        Assert.Equal(2, changed.Instance.Revision);
        Assert.Equal(state.Tenant.Revision + 1, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        Assert.Equal(count + 1, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
    }

    private static IReadOnlyDictionary<string, JsonElement> Fields<T>(T values) => JsonSerializer.SerializeToElement(values).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
}
