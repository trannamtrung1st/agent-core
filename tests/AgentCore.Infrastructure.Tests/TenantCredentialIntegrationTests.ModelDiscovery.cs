using AgentCore.Application.Models;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Models;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_discovery_is_atomic_preserves_overrides_and_never_enables_new_models(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var probe = new ModelMetadataProbe(); var service = Models(f, probe);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var credential = await f.Service.CreateAsync(human.PrincipalId, owner.TenantId,
            new("Inference", CredentialKind.ApiKey, CredentialPurpose.ModelInference, null, [], "private-model-key"), await Revision(), initiator: human);
        var connection = await service.SaveConnectionAsync(human, owner.TenantId, null,
            new(ModelProviderKind.OpenAI, "Team", null, credential.Credential.CredentialId, false, true), await Revision(), 0);
        var p = BuiltinModelPresets.All[0];
        var existing = await service.SaveOfferingAsync(human, owner.TenantId, null,
            new(connection.ConnectionId, p.PresetId, p.ExternalModelId, "Keep this operator label", p.Transport, p.Capabilities, true, false), await Revision(), 0);
        probe.Result = new(true, "metadata_available", [new(p.ExternalModelId, "Remote rename"), new("new-model", "New discovery")]);
        var tested = await service.TestAsync(human, owner.TenantId, connection.ConnectionId, true, await Revision(), connection.Revision);
        Assert.True(tested.MetadataAvailable); Assert.Equal(2, tested.ModelsObserved);
        var view = await service.ReadAsync(human, owner.TenantId);
        var retained = view.Offerings.Single(o => o.OfferingId == existing.OfferingId);
        Assert.Equal(existing.Label, retained.Label); Assert.Equal(System.Text.Json.JsonSerializer.Serialize(existing.Capabilities), System.Text.Json.JsonSerializer.Serialize(retained.Capabilities));
        Assert.Equal(existing.Revision, retained.Revision); Assert.True(retained.Enabled); Assert.Equal(ModelObservation.Observed, retained.Observation);
        var discovered = view.Offerings.Single(o => o.ExternalModelId == "new-model");
        Assert.False(discovered.Enabled); Assert.False(discovered.Capabilities.Tools);
        Assert.Equal(ModelInstructionHierarchy.Unsupported, discovered.Capabilities.Hierarchy);
        Assert.Null(view.Settings.DefaultOfferingId); Assert.Equal(connection.Revision, Assert.Single(view.Connections).Revision);
        await service.TestAsync(human, owner.TenantId, connection.ConnectionId, true, await Revision(), connection.Revision);
        Assert.Equal(discovered.OfferingId, (await service.ReadAsync(human, owner.TenantId)).Offerings.Single(o => o.ExternalModelId == "new-model").OfferingId);
        probe.Result = new(false, "metadata_unavailable", []);
        Assert.False((await service.TestAsync(human, owner.TenantId, connection.ConnectionId, true, await Revision(), connection.Revision)).MetadataAvailable);
        view = await service.ReadAsync(human, owner.TenantId);
        Assert.Equal(2, view.Offerings.Count); Assert.All(view.Offerings, o => Assert.Equal(ModelObservation.Observed, o.Observation));
        Assert.Equal("metadata_unavailable", Assert.Single(view.Connections).LastTestResult);
        probe.Result = new(true, "metadata_available", [new("new-model")]);
        await service.TestAsync(human, owner.TenantId, connection.ConnectionId, true, await Revision(), connection.Revision);
        retained = (await service.ReadAsync(human, owner.TenantId)).Offerings.Single(o => o.OfferingId == existing.OfferingId);
        Assert.Equal(ModelObservation.NotReturned, retained.Observation); Assert.True(retained.Enabled); Assert.Equal(existing.Revision, retained.Revision);
        var before = await service.ReadAsync(human, owner.TenantId);
        var unavailable = before.Availability.Single(a => a.OfferingId == existing.OfferingId);
        Assert.False(unavailable.Available); Assert.Equal("not_returned", unavailable.Reason);
        var sourceStore = (ITenantModelStore)f.Iam;
        var catalog = await new TenantModelCatalogReader(sourceStore, f.Iam).ReadAsync((await f.Iam.ReadTenantAsync(owner.TenantId))!);
        Assert.Null(catalog.Get(existing.OfferingId.ToString("D")));
        var route = ModelRouteRules.Pin((await sourceStore.FindModelRouteAsync(owner.TenantId, existing.OfferingId))!);
        var refused = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () => await new TenantModelRouteAuthority(f.Iam, sourceStore).RequireAsync(route, Invocation(route), ModelPurpose.Conversation));
        Assert.Equal("ModelUnavailable", refused.Code);

        probe.Result = new(true, "metadata_available", [new("must-not-be-committed")]);
        probe.BeforeReturn = async () => { await f.Service.ReplaceAsync(human.PrincipalId, owner.TenantId, credential.Credential.CredentialId, "rotated-model-key", await Revision(), 1, initiator: human); };
        await Assert.ThrowsAsync<SecurityConflictException>(async () => await service.TestAsync(human, owner.TenantId, connection.ConnectionId, true, before.SecurityRevision, connection.Revision));
        var after = await service.ReadAsync(human, owner.TenantId);
        Assert.DoesNotContain(after.Offerings, o => o.ExternalModelId == "must-not-be-committed");
        Assert.Equal(before.Connections.Single(), after.Connections.Single());
        if (sqlite)
        {
            var reopened = await new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!).ReadTenantModelsAsync(owner.TenantId);
            Assert.Equal(2, reopened.Offerings.Count); Assert.Equal(ModelObservation.NotReturned, reopened.Offerings.Single(o => o.OfferingId == existing.OfferingId).Observation);
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_manual_default_recovery_is_explicit_revisioned_and_does_not_rewrite_pinned_routes(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var setup = await ProviderSetup(f, ModelProviderKind.OpenAICompatible);
        var tenant = setup.Route.TenantId; var human = setup.Human;
        var probe = new ModelMetadataProbe(); var service = Models(f, probe);
        var store = (ITenantModelStore)f.Iam;
        var reader = new TenantModelCatalogReader(store, f.Iam);
        var authority = new TenantModelRouteAuthority(f.Iam, store);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        async Task<IModelCatalog> Catalog() => await reader.ReadAsync((await f.Iam.ReadTenantAsync(tenant))!);
        ModelOfferingChange Change(ModelOffering o, bool restore = false) => new(o.ConnectionId, o.PresetId, o.ExternalModelId,
            o.Label, o.Transport, o.Capabilities, o.Enabled, o.Retired, restore);
        async Task Missing() => await service.TestAsync(human, tenant, setup.Connection.ConnectionId, true, await Revision(), setup.Connection.Revision);
        await service.SaveDefaultAsync(human, tenant, setup.Offering.OfferingId, null, await Revision(), 0);
        var initiator = new TenantSessionInitiator(human.AccountId, human.AuthenticationSessionId, human.PrincipalId);
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var sessions = (ITenantSessionStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var sessionService = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, sessions, runs, (ITenantDefinitionStore)f.Iam,
            f.BuiltIns, f.Identity, f.Authorizer, reader, new(), f.Ids, f.Clock);
        var session = await sessionService.CreateAsync(tenant, agent.Instance.InstanceId, initiator, AgentCore.Domain.Conversation.SessionMode.Text, await Revision(), agent.Instance.Revision);
        var runService = new TenantRunService(f.Iam, sessions, runs, f.RunConfiguration(), f.Identity, f.Authorizer, reader, f.Ids, f.Clock);
        var receipt = await runService.SubmitTextAsync(tenant, session.Owner.SessionId, initiator, f.Ids.NewId(), "Retain my exact model", await Revision(), 1);
        var admitted = (await runs.FindTenantRunAsync(tenant, receipt.RunId))!;
        var pinned = admitted.PinnedModel.Binding!.Route;
        await authority.RequireAsync(pinned, Invocation(pinned), ModelPurpose.Conversation);
        await Missing();
        var missing = (await service.ReadAsync(human, tenant)).Offerings.Single();
        Assert.Null(missing.PresetId); Assert.Equal(ModelObservation.NotReturned, missing.Observation);
        Assert.Equal(setup.Offering.OfferingId.ToString("D"), (await Catalog()).DefaultOfferingId);
        var unavailableCatalog = await Catalog();
        Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() => SessionModelBinder.PinDefault(unavailableCatalog));
        var refused = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () =>
            await authority.RequireAsync(pinned, Invocation(pinned), ModelPurpose.Conversation));
        Assert.Equal("ModelUnavailable", refused.Code);
        // An ordinary edit cannot silently clear discovery evidence (or evade the default guard).
        await Assert.ThrowsAsync<SecurityConflictException>(async () => await service.SaveOfferingAsync(human, tenant,
            missing.OfferingId, Change(missing), await Revision(), missing.Revision));
        var stale = await Revision(); await Missing();
        await Assert.ThrowsAsync<SecurityConflictException>(async () => await service.SaveOfferingAsync(human, tenant,
            missing.OfferingId, Change(missing, true), stale, missing.Revision));
        var restored = await service.SaveOfferingAsync(human, tenant, missing.OfferingId, Change(missing, true), await Revision(), missing.Revision);
        Assert.Equal(ModelObservation.Unknown, restored.Observation); Assert.Equal(missing.LastSeenAt, restored.LastSeenAt);
        Assert.Equal(missing.OfferingId, restored.OfferingId); Assert.Equal(missing.ExternalModelId, restored.ExternalModelId);
        var current = SessionModelBinder.PinDefault(await Catalog()).Binding!.Route;
        Assert.Equal(pinned.OfferingId, current.OfferingId); Assert.Equal(pinned.ConnectionId, current.ConnectionId);
        Assert.Equal(pinned.OfferingRevision + 1, current.OfferingRevision);
        var stalePin = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () =>
            await authority.RequireAsync(pinned, Invocation(pinned), ModelPurpose.Conversation));
        Assert.Equal("ModelRouteStale", stalePin.Code);
        await authority.RequireAsync(current, Invocation(current), ModelPurpose.Conversation);
        Assert.Equal(admitted.PinnedModel.Binding, (await runs.FindTenantRunAsync(tenant, receipt.RunId))!.PinnedModel.Binding);
        Assert.Equal(pinned, (await sessions.FindTenantSessionAsync(tenant, session.Owner.SessionId))!.Snapshot.ModelSelection!.Binding!.Route);
        var nextSession = await sessionService.CreateAsync(tenant, agent.Instance.InstanceId, initiator, AgentCore.Domain.Conversation.SessionMode.Text, await Revision(), agent.Instance.Revision);
        Assert.Equal(current, (await sessions.FindTenantSessionAsync(tenant, nextSession.Owner.SessionId))!.Snapshot.ModelSelection!.Binding!.Route);
        if (sqlite)
        {
            var reopened = new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!);
            var catalog = await new TenantModelCatalogReader(reopened, reopened).ReadAsync((await reopened.ReadTenantAsync(tenant))!);
            Assert.Equal(current, SessionModelBinder.PinDefault(catalog).Binding!.Route);
            Assert.Equal(admitted.PinnedModel.Binding, (await reopened.FindTenantRunAsync(tenant, receipt.RunId))!.PinnedModel.Binding);
            Assert.Equal(ModelObservation.Unknown, Assert.Single((await reopened.ReadTenantModelsAsync(tenant)).Offerings).Observation);
        }
        await Missing();
        var blocked = await Catalog(); Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() => SessionModelBinder.PinDefault(blocked));
        probe.Result = new(true, "metadata_available", [new(restored.ExternalModelId)]);
        await Missing();
        Assert.Equal(ModelObservation.Observed, Assert.Single((await service.ReadAsync(human, tenant)).Offerings).Observation);
        Assert.Equal(current, SessionModelBinder.PinDefault(await Catalog()).Binding!.Route);
        await authority.RequireAsync(current, Invocation(current), ModelPurpose.Conversation);
    }

    private sealed class ModelMetadataProbe : IModelMetadataTransport
    {
        internal ModelMetadataResult Result { get; set; } = new(true, "metadata_available", []);
        internal Func<Task>? BeforeReturn { get; set; }
        public async ValueTask<ModelMetadataResult> ReadAsync(ProviderConnection connection, ModelCredentialWitness credential,
            bool discover, Func<CancellationToken, ValueTask> authorizeDispatch, CancellationToken ct = default)
        {
            await authorizeDispatch(ct); if (BeforeReturn is { } action) await action(); return Result;
        }
    }
}
