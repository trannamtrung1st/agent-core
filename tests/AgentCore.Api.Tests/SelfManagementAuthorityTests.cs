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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Policy_update_and_freeze_keep_inert_historical_preparation_bytes_private_and_unchanged(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "inert-harness-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using AgentCoreApiFactory host = sqlite ? new HarnessSqliteFactory(root) : new AgentCoreApiFactory();
            var services = host.Services;
            var owner = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 22);
            var history = new HarnessPreparation(Guid.NewGuid(), Guid.NewGuid(), owner.ActiveVersion, "Historical only", 1,
                HarnessPreparationStatus.AwaitingApproval,
                [new(Guid.NewGuid(), new string('a', 64), new("instructions.update", 1, Content: "historical-private-instructions"), "Pending")], []);
            var instances = services.GetRequiredService<IAgentInstanceStore>();
            // Seed retained historical data; this fixture does not authorize a preparation or execute it.
            owner = await instances.UpdateWithExpectedRevisionAsync(new(owner.InstanceId, owner.Revision,
                HarnessManagement: new(new(HarnessManagementMode.Managed, [HarnessManagementScope.Settings], [], []), Preparation: history)),
                services.GetRequiredService<TimeProvider>().GetUtcNow());
            var bytes = JsonSerializer.Serialize(owner.HarnessManagement!.Preparation);
            var policy = services.GetRequiredService<SelfManagementService>();
            var updated = await policy.UpdateAsync(owner.InstanceId, owner.Revision, new(Mode: new(HarnessManagementMode.Assisted)));
            Assert.Equal(HarnessManagementMode.Assisted, updated.Effective.Mode);
            Assert.DoesNotContain("historical-private-instructions", JsonSerializer.Serialize(updated));
            Assert.Equal(bytes, JsonSerializer.Serialize((await instances.FindAsync(owner.InstanceId))!.HarnessManagement!.Preparation));
            var frozen = await policy.FreezeAsync(owner.InstanceId, updated.InstanceRevision);
            Assert.True(frozen.Effective.Frozen);
            Assert.DoesNotContain("historical-private-instructions", JsonSerializer.Serialize(frozen));
            var retained = (await instances.FindAsync(owner.InstanceId))!;
            Assert.Equal(bytes, JsonSerializer.Serialize(retained.HarnessManagement!.Preparation));
            Assert.Equal("Pending", Assert.Single(retained.HarnessManagement.Preparation!.Approvals).Status);
        }
        finally { Directory.Delete(root, recursive: true); }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Builtin_choice_changes_only_that_field_and_new_definition_defaults_still_inherit(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "management-builtin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using AgentCoreApiFactory factory = sqlite ? new HarnessSqliteFactory(root) : new AgentCoreApiFactory();
            var services = factory.Services;
            var admin = services.GetRequiredService<AdminAgentInstanceService>();
            var owner = await admin.CreateManagedAsync("general-assistant", 23);
            var skills = services.GetRequiredService<AgentInstanceSkillService>();
            var builtin = await skills.InspectAsync(owner.InstanceId, ManagementBuiltInSkills.ConfigurationKey);
            await skills.WriteAsync(owner.InstanceId, "set_enabled", builtin.Key, builtin.Revision, enabled: false);
            var policy = await services.GetRequiredService<SelfManagementService>().ReadAsync(owner.InstanceId);
            Assert.Null(policy.Overrides!.Mode);
            Assert.Null(policy.Overrides.Scopes);
            Assert.Null(policy.Overrides.AutoApply);
            Assert.Null(policy.Overrides.HarnessAuthoring);
            Assert.False(policy.Overrides.ConfigurationAssistance!.Value);
            var lifecycle = services.GetRequiredService<AgentDefinitionLifecycleService>();
            var draft = await lifecycle.ForkDraftAsync("general-assistant", 23, DefinitionDraftSourceKind.ForkBuiltIn);
            var candidate = HarnessLegacyFixture.WithoutRetiredSandboxTool(draft.Candidate);
            draft = await lifecycle.UpdateDraftAsync(draft.DraftId, draft.Revision, candidate with {
                Environment = candidate.Environment! with { KnowledgeSources = [] },
                SelfManagement = new(HarnessManagementMode.Managed, [HarnessManagementScope.Settings], [], []) });
            var published = await services.GetRequiredService<AgentDefinitionDraftPublishService>().PublishDraftAsync(draft.DraftId, draft.Revision);
            await admin.ReassociateActiveVersionAsync(owner.InstanceId, published.Version, policy.InstanceRevision);
            policy = await services.GetRequiredService<SelfManagementService>().ReadAsync(owner.InstanceId);
            Assert.Equal(HarnessManagementMode.Managed, policy.Effective.Mode);
            Assert.Equal([HarnessManagementScope.Settings], policy.Effective.Scopes);
            Assert.False(policy.Effective.BuiltInSkills!.ConfigurationAssistance);
            builtin = await skills.InspectAsync(owner.InstanceId, ManagementBuiltInSkills.ConfigurationKey);
            await skills.WriteAsync(owner.InstanceId, "reset_enabled", builtin.Key, builtin.Revision);
            policy = await services.GetRequiredService<SelfManagementService>().ReadAsync(owner.InstanceId);
            Assert.Equal("definition", policy.Source);
            Assert.True(policy.Effective.BuiltInSkills!.ConfigurationAssistance);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Freezing_fresh_owner_preserves_all_definition_inheritance(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "management-freeze-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using AgentCoreApiFactory host = sqlite ? new HarnessSqliteFactory(root) : new AgentCoreApiFactory();
            var lifecycle = host.Services.GetRequiredService<AgentDefinitionLifecycleService>();
            var draft = await lifecycle.ForkDraftAsync("general-assistant", 23, DefinitionDraftSourceKind.ForkBuiltIn);
            var candidate = HarnessLegacyFixture.WithoutRetiredSandboxTool(draft.Candidate);
            draft = await lifecycle.UpdateDraftAsync(draft.DraftId, draft.Revision, candidate with {
                Environment = candidate.Environment! with { KnowledgeSources = [] },
                SelfManagement = new(HarnessManagementMode.Managed, [HarnessManagementScope.Settings], [], []) });
            var published = await host.Services.GetRequiredService<AgentDefinitionDraftPublishService>().PublishDraftAsync(draft.DraftId, draft.Revision);
            var owner = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", published.Version);
            var policy = await host.Services.GetRequiredService<SelfManagementService>().FreezeAsync(owner.InstanceId, owner.Revision);
            Assert.True(policy.Effective.Frozen);
            Assert.Equal("definition", policy.Source);
            Assert.Equal(new ManagementPolicyOverrides(), policy.Overrides);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Admin_builtin_catalog_discloses_missing_authority_without_runtime_grant()
    {
        await using var host = new AgentCoreApiFactory();
        var owner = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        var skill = await host.Services.GetRequiredService<AgentInstanceSkillService>().InspectAsync(owner.InstanceId, ManagementBuiltInSkills.ConfigurationKey);
        Assert.False(skill.Enabled);
        Assert.Contains("configuration.inspect", skill.MissingCapabilities);
        Assert.Equal(SkillOrigin.BuiltIn, skill.Origin);
        var definition = (await host.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync(owner.DefinitionId, owner.ActiveVersion))!;
        Assert.DoesNotContain(ManagementBuiltInSkills.Resolve(definition with { SelfManagement = new(HarnessManagementMode.Managed, [HarnessManagementScope.Settings], [], []) }),
            s => s.Key == ManagementBuiltInSkills.ConfigurationKey);
    }

}
