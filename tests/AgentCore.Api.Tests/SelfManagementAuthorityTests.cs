using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class SelfManagementAuthorityTests
{
    [Fact]
    public async Task Off_and_freeze_remove_skill_administration_and_revoke_previously_admitted_calls()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var owner = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 22);
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var definition = (await services.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId)).Configuration.Definition;
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: owner.InstanceId);
        var list = new ModelToolCall("list", "skills.list", "{}");
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), list, 100000, admission: admission)).Text);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(services));
        var endpoint = $"/api/v2/admin/agent-instances/{owner.InstanceId}/harness/policy";
        var enabled = await client.PutAsJsonAsync(endpoint, new HarnessPolicyRequest(owner.Revision, "Managed", ["Skills"], [], []));
        enabled.EnsureSuccessStatusCode();
        var review = (await enabled.Content.ReadFromJsonAsync<HarnessReviewResponse>())!;
        // An admitted Off Run cannot acquire the newly granted area.
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), list, 100000, admission: admission)).Text);
        definition = (await services.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId)).Configuration.Definition;
        var context = await executor.HarnessContextAsync(owner.InstanceId, default);
        admission = admission with { Harness = context };
        Assert.DoesNotContain("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), list, 100000, admission: admission)).Text);
        var frozen = await client.PutAsJsonAsync(endpoint, new HarnessPolicyRequest(review.InstanceRevision, "Disabled", [], [], [], Frozen: true));
        frozen.EnsureSuccessStatusCode();
        Assert.Null(await executor.HarnessContextAsync(owner.InstanceId, default));
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), list, 100000, admission: admission)).Text);
        // Normal use is independent of the management switch.
        Assert.Equal(ToolPolicyDecision.Allow, ToolPolicy.EvaluateExecution(definition, ToolCatalog.SkillsLoad,
            ToolConfigurationGates.Unconfigured, admission: admission));
    }

    [Fact]
    public async Task Sparse_policy_inherits_per_field_and_freeze_wins_over_new_definition_defaults()
    {
        await using var factory = new AgentCoreApiFactory();
        var definition = (await factory.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 22))! with { SelfManagement = new(HarnessManagementMode.Managed,
            [HarnessManagementScope.Settings], [], [], ManagedAutoApply: [ManagementAutoApply.ConversationPreferences]) };
        var now = DateTimeOffset.UtcNow;
        var owner = new AgentInstance(Guid.NewGuid(), definition.Id, 1, definition.Identity, AgentInstanceLifecycle.Active, now, now,
            HarnessManagement: new(HarnessManagementPolicy.Disabled with { Frozen = true }, Overrides: new(Mode: new(HarnessManagementMode.Assisted), ConfigurationAssistance: new(false))));
        var effective = ManagementPolicyResolver.Resolve(definition, owner);
        Assert.Equal(HarnessManagementMode.Assisted, effective.Mode);
        Assert.Equal(definition.SelfManagement.Scopes, effective.Scopes);
        Assert.False(effective.BuiltInSkills!.ConfigurationAssistance);
        Assert.True(effective.Frozen);
        Assert.False(effective.Allows(HarnessManagementScope.Settings));
        owner = owner with { HarnessManagement = new(new(HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources], [], [])) };
        effective = ManagementPolicyResolver.Resolve(definition, owner);
        Assert.False(effective.Allows(HarnessManagementScope.Skills));
        Assert.False(effective.Allows(HarnessManagementScope.Settings));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admin_policy_reset_and_definition_adoption_preserve_instance_freeze_on_reopen(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "management-adoption-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Guid id;
        try
        {
            await using (AgentCoreApiFactory factory = sqlite ? new HarnessSqliteFactory(root) : new AgentCoreApiFactory())
            {
                var services = factory.Services;
                var admin = services.GetRequiredService<AdminAgentInstanceService>();
                var owner = await admin.CreateManagedAsync("general-assistant", 22); id = owner.InstanceId;
                var service = services.GetRequiredService<SelfManagementService>();
                Assert.Equal(HarnessManagementMode.Disabled, (await service.ReadAsync(id)).Effective.Mode);
                var changed = await service.UpdateAsync(id, owner.Revision, new(new(HarnessManagementMode.Managed),
                    new([HarnessManagementScope.Settings]), ConfigurationAssistance: new(false)));
                Assert.False(changed.Effective.BuiltInSkills!.ConfigurationAssistance);
                var reset = await service.UpdateAsync(id, changed.InstanceRevision, new());
                Assert.Equal("definition", reset.Source);
                Assert.Equal(HarnessManagementMode.Disabled, reset.Effective.Mode);
                Assert.True(reset.Effective.BuiltInSkills!.ConfigurationAssistance);
                changed = await service.UpdateAsync(id, reset.InstanceRevision, new(new(HarnessManagementMode.Assisted),
                    new([HarnessManagementScope.Settings])), frozen: true);
                await admin.ReassociateActiveVersionAsync(id, 23, changed.InstanceRevision);
                var adopted = await service.ReadAsync(id);
                Assert.True(adopted.Effective.Frozen);
                Assert.Equal(HarnessManagementMode.Assisted, adopted.Effective.Mode);
                Assert.False(adopted.Effective.Allows(HarnessManagementScope.Settings));
            }
            if (sqlite)
            {
                await using var reopened = new HarnessSqliteFactory(root);
                Assert.True((await reopened.Services.GetRequiredService<SelfManagementService>().ReadAsync(id)).Effective.Frozen);
                Assert.Equal(23, (await reopened.Services.GetRequiredService<IAgentInstanceStore>().FindAsync(id))!.ActiveVersion);
            }
        }
        finally { Directory.Delete(root, true); }
    }

}
