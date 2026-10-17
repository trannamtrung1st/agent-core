using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_version_preview_rejects_incompatible_retained_overrides_without_changing_state(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Scheduling policy", await Revision());
        var policy = new TriggerPolicy(true, true, true, true, true, false, 4, 30, 1, ["schedule"]);
        draft = await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, draft.Candidate with { TriggerPolicy = policy }, await Revision(), draft.Revision);
        var definitionId = Guid.Parse(draft.DefinitionId);
        await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision);
        var agent = await agents.CreateFromPublicationAsync(owner.OwnerPrincipalId, owner.TenantId, definitionId, 1, await Revision()); var id = agent.Instance.InstanceId;
        await configuration.PatchSettingsAsync(owner.OwnerPrincipalId, owner.TenantId, id, "triggerPolicy", await Revision(), 1, Fields(new { allowDaily = true }), []);
        await configuration.UpdateManagementAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), 2,
            new(Mode: new(HarnessManagementMode.Disabled), ConfigurationAssistance: new(false)), frozen: true);
        var choices = (await configuration.PreviewVersionAsync(owner.OwnerPrincipalId, owner.TenantId, id, 1)).Preview.PreservedInstanceOverrides;
        Assert.Contains("selfManagement.mode", choices); Assert.Contains("selfManagement.configurationAssistance", choices); Assert.Contains("selfManagement.frozen", choices);
        Assert.DoesNotContain("selfManagement.scopes", choices); Assert.Contains("triggerPolicy.allowDaily", choices);
        var fork = await f.Service.ForkAsync(owner.OwnerPrincipalId, owner.TenantId, definitionId, 1, await Revision());
        fork = await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId, fork.Candidate with { TriggerPolicy = policy with { AllowDaily = false } }, await Revision(), fork.Revision);
        await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId, await Revision(), fork.Revision);
        var before = await Revision(); var prior = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, id))!;
        var previewError = await Assert.ThrowsAsync<ArgumentException>(() => configuration.PreviewVersionAsync(owner.OwnerPrincipalId, owner.TenantId, id, 2).AsTask());
        Assert.Equal("The target version is incompatible with the current Instance configuration.", previewError.Message);
        Assert.Equal(400, (await Assert.ThrowsAsync<AgentCoreException>(() => configuration.AdoptVersionAsync(owner.OwnerPrincipalId, owner.TenantId, id, 2, before, prior.Instance.Revision).AsTask())).StatusCode);
        Assert.Equal(before, await Revision()); Assert.Equal(System.Text.Json.JsonSerializer.Serialize(prior), System.Text.Json.JsonSerializer.Serialize(await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, id)));
        var p2 = (await f.Service.PublicationsAsync(owner.OwnerPrincipalId, owner.TenantId, definitionId)).Single(p => p.Version == 2);
        await f.Service.DeprecateAsync(owner.OwnerPrincipalId, owner.TenantId, definitionId, 2, await Revision(), p2.MetadataRevision);
        var catalog = await configuration.ReadVersionsAsync(owner.OwnerPrincipalId, owner.TenantId, id);
        Assert.Equal("Deprecated", catalog.Items.Single(p => p.Version == 2).Status);
        await Assert.ThrowsAsync<AccessDeniedException>(() => configuration.PreviewVersionAsync(owner.OwnerPrincipalId, owner.TenantId, id, 2).AsTask());
    }
}
