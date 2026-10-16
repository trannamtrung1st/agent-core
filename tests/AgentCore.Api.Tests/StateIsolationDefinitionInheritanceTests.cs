using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class StateIsolationDefinitionInheritanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_definition_policy_fields_survive_publication_and_adoption_while_activation_remains_instance_owned(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), $"isolation-inheritance-{Guid.NewGuid():N}.db");
        Guid ownerId, siblingId;
        var policy = new StateIsolationPolicy(HomeAccess: HomeAccess.ReadOnly, WorkingCleanup: WorkingCleanup.AfterTerminal,
            BrowserIsolation: BrowserIsolation.EphemeralSession, AgentStateVisibility: AgentStateVisibility.CurrentSession,
            MaxAgentStateVisibility: AgentStateVisibility.InstanceSummary);
        var nextPolicy = policy with { HomeAccess = HomeAccess.ReadWrite, WorkingCleanup = WorkingCleanup.OnSessionDeletion,
            BrowserIsolation = BrowserIsolation.HostDefault, AgentStateVisibility = AgentStateVisibility.InstanceSummary };
        await using (WebApplicationFactory<Program> host = sqlite ? new ExperienceHost(path) : new AgentCoreApiFactory())
        {
            using var client = TestOwnerCapability.CreateOwnerClient(host);
            var lifecycle = host.Services.GetRequiredService<AgentDefinitionLifecycleService>();
            var publish = host.Services.GetRequiredService<AgentDefinitionDraftPublishService>();
            var instances = host.Services.GetRequiredService<AdminAgentInstanceService>();
            var isolation = host.Services.GetRequiredService<StateIsolationConfigurationService>();
            var settings = host.Services.GetRequiredService<AgentInstanceSettingsService>();
            var draft = await lifecycle.ForkDraftAsync("approval-demo", 1, DefinitionDraftSourceKind.ForkBuiltIn);
            draft = await lifecycle.UpdateDraftAsync(draft.DraftId, draft.Revision, draft.Candidate with { StateIsolationPolicy = policy });
            Assert.Equal(policy, (await lifecycle.GetDraftAsync(draft.DraftId)).Candidate.StateIsolationPolicy);
            var first = await publish.PublishDraftAsync(draft.DraftId, draft.Revision);
            var owner = await instances.CreateManagedAsync(first.DefinitionId, first.Version);
            ownerId = owner.InstanceId;
            var view = await isolation.ReadAsync(ownerId);
            AssertPolicy(view, policy);
            Assert.False(view.ExperienceEnabled);
            Assert.False(view.AllowMaintenance);

            // Activation is an explicit write to the same Instance's independent owners.
            var activate = await isolation.PreviewAsync(ownerId, new([], true, true));
            view = await isolation.ApplyAsync(ownerId, activate.Expected, activate.Draft, activate.Fingerprint);
            Assert.True(view.ExperienceEnabled);
            Assert.True(view.AllowMaintenance);
            var narrowed = await settings.PatchAsync(ownerId, "stateIsolationPolicy", view.Revisions.Instance,
                new Dictionary<string, JsonElement> { ["profileEvidence"] = JsonSerializer.SerializeToElement(false) }, []);

            draft = await lifecycle.ForkDraftAsync(first.DefinitionId, first.Version, DefinitionDraftSourceKind.ForkDurable);
            draft = await lifecycle.UpdateDraftAsync(draft.DraftId, draft.Revision, draft.Candidate with { StateIsolationPolicy = nextPolicy });
            var second = await publish.PublishDraftAsync(draft.DraftId, draft.Revision);
            view = await isolation.ReadAsync(ownerId);
            Assert.Equal(first.Version, view.Settings.Single(s => s.Section == "stateIsolationPolicy").DefinitionVersion);
            AssertPolicy(view, policy, profileOverride: false);

            // Publication alone cannot move an existing Instance; adoption preserves narrowing and activation.
            await instances.ReassociateActiveVersionAsync(ownerId, second.Version, narrowed.InstanceRevision);
            view = await isolation.ReadAsync(ownerId);
            Assert.Equal(second.Version, view.Settings.Single(s => s.Section == "stateIsolationPolicy").DefinitionVersion);
            AssertPolicy(view, nextPolicy, profileOverride: false);
            Assert.True(view.ExperienceEnabled);
            Assert.True(view.AllowMaintenance);
            await settings.PatchAsync(ownerId, "stateIsolationPolicy", view.Revisions.Instance,
                new Dictionary<string, JsonElement>(), ["profileEvidence"]);
            view = await isolation.ReadAsync(ownerId);
            AssertPolicy(view, nextPolicy);
            Assert.True(view.ExperienceEnabled);
            Assert.True(view.AllowMaintenance);

            var sibling = await instances.CreateManagedAsync(second.DefinitionId, second.Version);
            siblingId = sibling.InstanceId;
            view = await isolation.ReadAsync(siblingId);
            AssertPolicy(view, nextPolicy);
            Assert.False(view.ExperienceEnabled);
            Assert.False(view.AllowMaintenance);
        }
        if (sqlite)
        {
            await using var reopened = new ExperienceHost(path);
            using var client = TestOwnerCapability.CreateOwnerClient(reopened);
            var isolation = reopened.Services.GetRequiredService<StateIsolationConfigurationService>();
            var owner = await isolation.ReadAsync(ownerId);
            AssertPolicy(owner, nextPolicy);
            Assert.True(owner.ExperienceEnabled);
            Assert.True(owner.AllowMaintenance);
            var sibling = await isolation.ReadAsync(siblingId);
            AssertPolicy(sibling, nextPolicy);
            Assert.False(sibling.ExperienceEnabled);
            Assert.False(sibling.AllowMaintenance);
        }
    }

    private static void AssertPolicy(IsolationConfigurationView view, StateIsolationPolicy expected, bool? profileOverride = null)
    {
        var state = view.Settings.Single(s => s.Section == "stateIsolationPolicy");
        var values = new Dictionary<string, object?> {
            ["conversationContextScope"] = expected.ConversationContextScope.ToString(), ["homeAccess"] = expected.HomeAccess.ToString(),
            ["workingCleanup"] = expected.WorkingCleanup.ToString(), ["browserIsolation"] = expected.BrowserIsolation.ToString(),
            ["agentStateVisibility"] = expected.AgentStateVisibility.ToString(), ["experienceRecord"] = expected.ExperienceRecord,
            ["experienceRecall"] = expected.ExperienceRecall, ["profileEvidence"] = expected.ProfileEvidence,
            ["crossSessionContinuity"] = expected.CrossSessionContinuity
        };
        Assert.Equal(values.Count, state.Effective.Count);
        foreach (var (field, value) in values)
        {
            Assert.Equal(value, state.DefinitionDefaults[field]);
            Assert.Equal(field == "profileEvidence" && profileOverride.HasValue ? profileOverride.Value : value, state.Effective[field]);
            Assert.Equal(field == "profileEvidence" && profileOverride.HasValue ? "instance" : "definition", state.Sources[field]);
        }
        if (profileOverride.HasValue) Assert.Single(state.Overrides);
        else Assert.Empty(state.Overrides);
    }
}
