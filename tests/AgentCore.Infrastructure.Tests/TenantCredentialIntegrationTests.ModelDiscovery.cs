using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
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
