using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

// Precondition unowned native fixtures only. These helpers have no actor/Run/approval authority.
// Canonical mutation and disclosure are qualified separately through tenant HTTP/model tests.
internal static class NativeInstanceConfigurationSeed
{
    internal static async ValueTask<AgentInstance> CreateAsync(IServiceProvider services, string definitionId, int version)
    {
        var definition = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync(definitionId, version))!;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var instance = new AgentInstance(services.GetRequiredService<IIdGenerator>().NewId(), definition.Id, definition.Version,
            definition.Identity, AgentInstanceLifecycle.Active, now, now);
        await services.GetRequiredService<IAgentInstanceStore>().InsertAsync(instance, initialSkills: definition.SkillList);
        return instance;
    }
    internal static async ValueTask<AgentInstance> AdoptAsync(IServiceProvider services, Guid id, int version, long revision)
    {
        var (store, owner, _) = await Read(services, id);
        var target = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync(owner.DefinitionId, version))!;
        return await store.UpdateWithExpectedRevisionAsync(new(id, revision, ActiveVersion: version, DefinitionSkills: target.SkillList), services.GetRequiredService<TimeProvider>().GetUtcNow());
    }
    private static async ValueTask<(IAgentInstanceStore Store, AgentInstance Owner, AgentDefinition Definition)> Read(IServiceProvider services, Guid id)
    {
        var store = services.GetRequiredService<IAgentInstanceStore>();
        var owner = await store.FindAsync(id) ?? throw new InvalidOperationException("Expected an unowned native fixture.");
        var definition = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync(owner.DefinitionId, owner.ActiveVersion))!;
        return (store, owner, definition);
    }
    internal static async ValueTask<ManagementPolicyView> PolicyViewAsync(IServiceProvider services, Guid id)
    {
        var (_, owner, definition) = await Read(services, id);
        return HarnessManagementRules.View(owner, definition);
    }
    internal static async ValueTask<ManagementPolicyView> PolicyAsync(IServiceProvider services, Guid id, long revision, ManagementPolicyOverrides overrides, bool? frozen = null)
    {
        var (store, owner, definition) = await Read(services, id);
        var next = HarnessManagementRules.Prepare(owner, definition, overrides, frozen, agent: false);
        var saved = await store.UpdateWithExpectedRevisionAsync(new(id, revision, HarnessManagement: next), services.GetRequiredService<TimeProvider>().GetUtcNow());
        return HarnessManagementRules.View(saved, definition);
    }
    internal static async ValueTask<ManagementPolicyView> FreezeAsync(IServiceProvider services, Guid id, long revision)
    {
        var (store, owner, definition) = await Read(services, id);
        var saved = await store.UpdateWithExpectedRevisionAsync(new(id, revision, HarnessManagement: HarnessManagementRules.PrepareFreeze(owner, definition)), services.GetRequiredService<TimeProvider>().GetUtcNow());
        return HarnessManagementRules.View(saved, definition);
    }
    internal static async ValueTask<InstanceSettingsSection> SettingsViewAsync(IServiceProvider services, Guid id, string section)
    {
        var (_, owner, definition) = await Read(services, id);
        return InstanceSettingsRules.View(owner, definition, section);
    }
    internal static async ValueTask SettingsAsync(IServiceProvider services, Guid id, string section, long revision,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> set, IReadOnlyList<string> clear)
    {
        var (store, owner, definition) = await Read(services, id);
        var (next, _) = InstanceSettingsRules.ResolvePatch(definition, owner.SettingsOverrides ?? new(), section, set, clear, null, null);
        await store.UpdateWithExpectedRevisionAsync(new(id, revision, SettingsOverrides: next, SetSettingsOverrides: true), services.GetRequiredService<TimeProvider>().GetUtcNow());
    }
    internal static async ValueTask<IReadOnlyList<InstanceSkillView>> SkillsAsync(IServiceProvider services, Guid id)
    {
        var (store, owner, definition) = await Read(services, id);
        return InstanceSkillRules.Views(definition, await store.ReadSkillsAsync(id))
            .Concat(InstanceSkillRules.BuiltIns(definition, ManagementPolicyResolver.Resolve(definition, owner), owner)).ToArray();
    }
    internal static async ValueTask<InstanceSkillView> InspectSkillAsync(IServiceProvider services, Guid id, string key) =>
        (await SkillsAsync(services, id)).Single(s => s.Key == key);
    internal static async ValueTask WriteBuiltInChoiceAsync(IServiceProvider services, Guid id, string operation, string key, long revision, bool? enabled = null)
    {
        var (store, owner, definition) = await Read(services, id);
        if (key != ManagementBuiltInSkills.ConfigurationKey) throw new InvalidOperationException("This native fixture seeds only Configuration assistance.");
        if (operation is not ("set_enabled" or "reset_enabled"))
        {
            _ = InstanceSkillRules.Prepare(owner, definition, await store.ReadSkillsAsync(id), operation, key, revision, null, enabled, SkillAuthor.Admin, services.GetRequiredService<TimeProvider>().GetUtcNow());
            throw new InvalidOperationException("Built-in mutation unexpectedly prepared.");
        }
        var view = HarnessManagementRules.View(owner, definition);
        var overrides = view.Overrides ?? (view.Source == "instance" ? ManagementPolicyOverrides.FromLegacy(view.Effective) : new());
        var choice = operation == "reset_enabled" ? null : new InstanceField<bool>(enabled!.Value);
        overrides = overrides with { ConfigurationAssistance = choice };
        await PolicyAsync(services, id, owner.Revision, overrides);
    }
}
