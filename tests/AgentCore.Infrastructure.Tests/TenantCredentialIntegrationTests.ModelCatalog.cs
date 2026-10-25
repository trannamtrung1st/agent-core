using System.Text.Json;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Models;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_admin_configuration_remains_readable_without_a_default_then_projects_native_tenant_binding(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ProviderSetup(f, ModelProviderKind.Anthropic);
        var tenant = setup.Route.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(setup.Human.PrincipalId, tenant, "examiner", 1, await Revision());
        var configuration = f.AgentConfiguration(new TenantModelCatalogReader((ITenantModelStore)f.Iam, f.Iam));
        var unavailable = await configuration.ReadAsync(setup.Human.PrincipalId, tenant, agent.Instance.InstanceId);
        Assert.False(unavailable.Effective.EffectiveModel.Available);
        Assert.Equal("ModelUnavailable", unavailable.Effective.EffectiveModel.UnavailableReason);
        Assert.False(unavailable.Effective.DurableExecutionEligibility.CanAcceptNewTriggeredWork);
        await Models(f).SaveDefaultAsync(setup.Human, tenant, setup.Offering.OfferingId, "low", await Revision(), 0);
        var ready = await configuration.ReadAsync(setup.Human.PrincipalId, tenant, agent.Instance.InstanceId);
        Assert.True(ready.Effective.EffectiveModel.Available); Assert.Null(ready.Effective.EffectiveModel.UnavailableReason);
        Assert.Equal(setup.Offering.OfferingId.ToString("D"), ready.Effective.EffectiveModel.OfferingId);
        Assert.Equal("tenantDefault", ready.Effective.EffectiveModel.SelectionSource);
        Assert.Equal("low", ready.Effective.EffectiveModel.ReasoningEffort);
        Assert.DoesNotContain("factory-private-key", JsonSerializer.Serialize(ready));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_catalog_is_empty_until_explicit_setup_and_has_no_arbitrary_default(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var source = (ITenantModelStore)f.Iam; var reader = new TenantModelCatalogReader(source, f.Iam);
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var empty = Assert.IsType<TenantModelCatalog>(await reader.ReadAsync(state));
        Assert.Empty(empty.Models); Assert.Equal("", empty.DefaultOfferingId); Assert.Null(empty.DefaultReasoningEffort);
        Assert.Equal("ModelUnavailable", Assert.Throws<AgentCoreException>(() => SessionModelBinder.PinDefault(empty)).Code);
        Assert.Equal(7, (await source.ReadModelPresetsAsync()).Count);
        var settings = await source.ReadTenantModelsAsync(owner.TenantId);
        Assert.Empty(settings.Connections); Assert.Empty(settings.Offerings); Assert.Null(settings.Settings.DefaultOfferingId);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_catalog_captures_exact_routes_default_effort_and_native_hierarchy_without_alias_fallback(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ProviderSetup(f, ModelProviderKind.Anthropic);
        var reader = new TenantModelCatalogReader((ITenantModelStore)f.Iam, f.Iam); var service = Models(f);
        async Task<AgentCore.Domain.Security.TenantSecurityState> State() => (await f.Iam.ReadTenantAsync(setup.Route.TenantId))!;
        var noDefault = await reader.ReadAsync(await State());
        Assert.Single(noDefault.Models); Assert.Throws<AgentCoreException>(() => SessionModelBinder.PinDefault(noDefault));
        var explicitSelection = SessionModelBinder.Bind(noDefault, setup.Offering.OfferingId.ToString("D"), "high", ModelSelectionSource.User);
        Assert.Equal("high", explicitSelection.ReasoningEffort); Assert.Equal(ModelSelectionSource.User, explicitSelection.SelectionSource);
        Assert.Equal(setup.Route, explicitSelection.Binding!.Route);
        var descriptor = Assert.Single(noDefault.Models); Assert.False(descriptor.DeveloperRoleSupport); Assert.True(descriptor.SupportsInstructionHierarchy);
        Assert.Equal(setup.Route, descriptor.Route); Assert.Equal("Exact connection", descriptor.ConnectionLabel);
        await service.SaveDefaultAsync(setup.Human, setup.Route.TenantId, setup.Offering.OfferingId, "low", (await State()).Tenant.Revision, 0);
        var pinned = Assert.IsType<TenantModelCatalog>(await reader.ReadAsync(await State()));
        Assert.Equal("low", SessionModelBinder.PinDefault(pinned).ReasoningEffort);
        Assert.Equal(ModelSelectionSource.TenantDefault, SessionModelBinder.PinDefault(pinned).SelectionSource);
        Assert.Equal("tenantDefault", SessionModelBinder.ToWire(ModelSelectionSource.TenantDefault));
        Assert.Equal("medium", SessionModelBinder.Bind(pinned, setup.Offering.OfferingId.ToString("D"), null, ModelSelectionSource.User).ReasoningEffort);
        Assert.DoesNotContain("factory-private-key", JsonSerializer.Serialize(pinned));
        Assert.DoesNotContain("ProtectedPayload", JsonSerializer.Serialize(pinned));
        Assert.Throws<NotSupportedException>(() => ((IList<ModelDescriptor>)pinned.Models).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)descriptor.SupportedReasoningEfforts).Clear());
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!);
            var retained = Assert.IsType<TenantModelCatalog>(await new TenantModelCatalogReader(reopened, reopened).ReadAsync(await State()));
            Assert.Equal(pinned.DefaultOfferingId, retained.DefaultOfferingId); Assert.Equal(pinned.DefaultReasoningEffort, retained.DefaultReasoningEffort);
            Assert.Equal(descriptor.Route, Assert.Single(retained.Models).Route);
        }
        var staleState = await State();
        await f.Service.UpdateAsync(setup.Human.PrincipalId, setup.Route.TenantId, setup.Connection.CredentialId!.Value,
            new("Revoked inference", CredentialStatus.Disabled, null, []), staleState.Tenant.Revision, 1, initiator: setup.Human);
        await Assert.ThrowsAsync<SecurityConflictException>(async () => await reader.ReadAsync(staleState));
        var revoked = await reader.ReadAsync(await State()); Assert.Empty(revoked.Models);
        Assert.Throws<AgentCoreException>(() => SessionModelBinder.PinDefault(revoked));
        Assert.Single(pinned.Models); Assert.Equal(setup.Route, pinned.Models[0].Route); // Historical value stays immutable.
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_catalog_does_not_merge_connections_or_expose_another_tenant(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ProviderSetup(f, ModelProviderKind.OpenAI);
        var service = Models(f); var reader = new TenantModelCatalogReader((ITenantModelStore)f.Iam, f.Iam);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Route.TenantId))!.Tenant.Revision;
        var second = await service.SaveConnectionAsync(setup.Human, setup.Route.TenantId, null,
            new(setup.Connection.Kind, "Second connection", null, setup.Connection.CredentialId, false, true), await Revision(), 0);
        var secondOffering = await service.SaveOfferingAsync(setup.Human, setup.Route.TenantId, null,
            new(second.ConnectionId, setup.Offering.PresetId, setup.Offering.ExternalModelId, "Second offering", setup.Offering.Transport, setup.Offering.Capabilities, true, false), await Revision(), 0);
        var state = (await f.Iam.ReadTenantAsync(setup.Route.TenantId))!; var catalog = await reader.ReadAsync(state);
        Assert.Equal(2, catalog.Models.Count); Assert.Equal(2, catalog.Models.Select(d => d.Route!.ConnectionId).Distinct().Count());
        Assert.Equal(secondOffering.OfferingId, catalog.Get(secondOffering.OfferingId.ToString("D"))!.Route!.OfferingId);
        Assert.Null(catalog.Get(setup.Offering.ExternalModelId)); // No vendor-ID or legacy-key coercion.
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single();
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, state.Users.Single().AccountId, "Beta", "beta");
        var betaCatalog = await reader.ReadAsync(beta); Assert.Empty(betaCatalog.Models);
        Assert.Null(betaCatalog.Get(setup.Offering.OfferingId.ToString("D")));
        await service.SaveOfferingAsync(setup.Human, setup.Route.TenantId, secondOffering.OfferingId,
            new(second.ConnectionId, setup.Offering.PresetId, secondOffering.ExternalModelId, secondOffering.Label, secondOffering.Transport,
                secondOffering.Capabilities with { Hierarchy = ModelInstructionHierarchy.Unsupported }, true, false), await Revision(), 1);
        var current = await reader.ReadAsync((await f.Iam.ReadTenantAsync(setup.Route.TenantId))!);
        Assert.Single(current.Models); Assert.Equal(2, catalog.Models.Count);
    }
}
