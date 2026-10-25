using System.Text.Json;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    private static TenantModelManagementService Models(Fixture f, IModelMetadataTransport? metadata = null) => new(f.Iam, (ITenantModelStore)f.Iam, f.Credentials,
        f.Identity, f.Authorizer, new(), f.Ids, f.Clock, metadata);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_onboarding_is_explicit_owned_audited_guarded_and_durable(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var service = Models(f); var store = (ITenantModelStore)f.Iam;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var empty = await service.ReadAsync(human, owner.TenantId);
        Assert.Empty(empty.Connections); Assert.Empty(empty.Offerings); Assert.Null(empty.Settings.DefaultOfferingId);
        Assert.Equal(7, empty.Presets.Count); Assert.All(empty.Presets, p => Assert.True(p.Enabled));
        var credential = await f.Service.CreateAsync(human.PrincipalId, owner.TenantId,
            new("Inference", CredentialKind.ApiKey, CredentialPurpose.ModelInference, null, [], "private-model-key"), await Revision(), initiator: human);
        var connection = await service.SaveConnectionAsync(human, owner.TenantId, null,
            new(ModelProviderKind.OpenAI, "Team OpenAI", null, credential.Credential.CredentialId, false, true), await Revision(), 0);
        Assert.True(await f.Iam.OwnsResourceAsync(TenantModelRules.Scope(owner.TenantId, connection.ConnectionId, true)));
        Assert.Empty((await service.ReadAsync(human, owner.TenantId)).Offerings); // No automatic adoption/default.
        var preset = empty.Presets.First(p => p.Kind == ModelProviderKind.OpenAI);
        var offering = await service.SaveOfferingAsync(human, owner.TenantId, null,
            new(connection.ConnectionId, preset.PresetId, preset.ExternalModelId, preset.Label, preset.Transport, preset.Capabilities, true, false), await Revision(), 0);
        var settings = await service.SaveDefaultAsync(human, owner.TenantId, offering.OfferingId, "medium", await Revision(), 0);
        var view = await service.ReadAsync(human, owner.TenantId);
        Assert.Equal(offering.OfferingId, view.Settings.DefaultOfferingId); Assert.Equal("medium", view.Settings.DefaultReasoningEffort);
        Assert.DoesNotContain("private-model-key", JsonSerializer.Serialize(view));
        Assert.DoesNotContain("ProtectedPayload", JsonSerializer.Serialize(view));
        Assert.DoesNotContain("models.use", PermissionCatalog.All.Select(p => p.Key));
        var before = await Revision(); var audits = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var disabled = await Assert.ThrowsAsync<SecurityConflictException>(async () => await service.SaveConnectionAsync(human, owner.TenantId, connection.ConnectionId,
            new(connection.Kind, connection.Label, null, connection.CredentialId, false, false), before, connection.Revision).AsTask());
        Assert.Equal("model_connection_is_default", disabled.ReasonCode);
        var retired = await Assert.ThrowsAsync<SecurityConflictException>(async () => await service.SaveOfferingAsync(human, owner.TenantId, offering.OfferingId,
            new(offering.ConnectionId, offering.PresetId, offering.ExternalModelId, offering.Label, offering.Transport, offering.Capabilities, false, true), before, offering.Revision).AsTask());
        Assert.Equal("model_offering_is_default", retired.ReasonCode);
        var incompatible = await Assert.ThrowsAsync<SecurityConflictException>(async () => await service.SaveOfferingAsync(human, owner.TenantId, offering.OfferingId,
            new(offering.ConnectionId, offering.PresetId, offering.ExternalModelId, offering.Label, offering.Transport,
                offering.Capabilities with { Hierarchy = ModelInstructionHierarchy.Unsupported }, true, false), before, offering.Revision));
        Assert.Equal("model_default_unavailable", incompatible.ReasonCode);
        await Assert.ThrowsAsync<SecurityConflictException>(async () => await f.Service.DeleteAsync(human.PrincipalId, owner.TenantId, credential.Credential.CredentialId, before, 1, initiator: human).AsTask());
        Assert.Equal(before, await Revision()); Assert.Equal(audits, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        await Assert.ThrowsAsync<SecurityConflictException>(async () => await service.SaveDefaultAsync(human, owner.TenantId, offering.OfferingId, "low", before - 1, settings.Revision).AsTask());
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!);
            var persisted = await reopened.ReadTenantModelsAsync(owner.TenantId);
            Assert.Equal(offering.OfferingId, persisted.Settings.DefaultOfferingId); Assert.Equal(connection, Assert.Single(persisted.Connections));
            await using var db = f.Contexts!.CreateDbContext(); await db.Database.MigrateAsync();
            Assert.Equal(7, (await reopened.ReadModelPresetsAsync()).Count); Assert.False(db.Database.HasPendingModelChanges());
        }
        await service.SaveDefaultAsync(human, owner.TenantId, null, null, await Revision(), settings.Revision);
        var stopped = await service.SaveConnectionAsync(human, owner.TenantId, connection.ConnectionId,
            new(connection.Kind, connection.Label, null, connection.CredentialId, false, false), await Revision(), connection.Revision);
        Assert.False(stopped.Enabled);
        await Assert.ThrowsAsync<SecurityConflictException>(async () => await service.SaveDefaultAsync(human, owner.TenantId, offering.OfferingId, null, await Revision(), settings.Revision + 1).AsTask());
        // Inference keys can never become model/browser/tool-bound account resources.
        var protectedCredential = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, credential.Credential.CredentialId))!;
        Assert.Throws<AccessDeniedException>(() => TenantCredentialBindingRules.Bindable(protectedCredential.Value));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_cross_tenant_connections_defaults_and_original_login_are_fenced(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var service = Models(f);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single();
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        var betaHuman = human with { PrincipalId = beta.Users.Single().PrincipalId };
        async Task<long> Rev(Guid tenant) => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var credential = await f.Service.CreateAsync(human.PrincipalId, owner.TenantId,
            new("Inference", CredentialKind.ApiKey, CredentialPurpose.ModelInference, null, [], "private-model-key"), await Rev(owner.TenantId), initiator: human);
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.SaveConnectionAsync(betaHuman, beta.Tenant.TenantId, null,
            new(ModelProviderKind.OpenAI, "Guessed key", null, credential.Credential.CredentialId, false, true), await Rev(beta.Tenant.TenantId), 0).AsTask());
        var connection = await service.SaveConnectionAsync(human, owner.TenantId, null,
            new(ModelProviderKind.OpenAI, "Private team", null, credential.Credential.CredentialId, false, true), await Rev(owner.TenantId), 0);
        Assert.False(await f.Iam.OwnsResourceAsync(TenantModelRules.Scope(beta.Tenant.TenantId, connection.ConnectionId, true)));
        Assert.Empty((await service.ReadAsync(betaHuman, beta.Tenant.TenantId)).Connections);
        var p = BuiltinModelPresets.All[0];
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.SaveOfferingAsync(betaHuman, beta.Tenant.TenantId, null,
            new(connection.ConnectionId, p.PresetId, p.ExternalModelId, "Guessed", p.Transport, p.Capabilities, true, false), await Rev(beta.Tenant.TenantId), 0).AsTask());
        await f.Iam.RevokeSessionAsync(human.AuthenticationSessionId, f.Clock.GetUtcNow());
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.ReadAsync(human, owner.TenantId).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.SaveDefaultAsync(human, owner.TenantId, null, null, await Rev(owner.TenantId), 0).AsTask());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_external_identity_is_unique_per_connection_without_cross_connection_coercion(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var service = Models(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var credential = await f.Service.CreateAsync(human.PrincipalId, owner.TenantId,
            new("Inference", CredentialKind.ApiKey, CredentialPurpose.ModelInference, null, [], "private-model-key"), await Revision(), initiator: human);
        var first = await service.SaveConnectionAsync(human, owner.TenantId, null,
            new(ModelProviderKind.OpenAI, "First", null, credential.Credential.CredentialId, false, true), await Revision(), 0);
        var second = await service.SaveConnectionAsync(human, owner.TenantId, null,
            new(ModelProviderKind.OpenAI, "Second", null, credential.Credential.CredentialId, false, true), await Revision(), 0);
        var p = BuiltinModelPresets.All[0];
        ModelOfferingChange Input(Guid connection) => new(connection, p.PresetId, p.ExternalModelId, p.Label, p.Transport, p.Capabilities, true, false);
        var a = await service.SaveOfferingAsync(human, owner.TenantId, null, Input(first.ConnectionId), await Revision(), 0);
        var b = await service.SaveOfferingAsync(human, owner.TenantId, null, Input(second.ConnectionId), await Revision(), 0);
        Assert.NotEqual(a.OfferingId, b.OfferingId); Assert.Equal(a.ExternalModelId, b.ExternalModelId);
        var before = await Revision();
        await Assert.ThrowsAsync<SecurityConflictException>(async () => await service.SaveOfferingAsync(human, owner.TenantId, null, Input(first.ConnectionId), before, 0));
        Assert.Equal(before, await Revision()); Assert.Equal(2, (await service.ReadAsync(human, owner.TenantId)).Offerings.Count);
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext();
            var retained = await db.Set<ModelOfferingRecord>().SingleAsync(o => o.OfferingId == a.OfferingId);
            db.Entry(retained).CurrentValues.SetValues(retained with { Kind = ModelProviderKind.Anthropic });
            await Assert.ThrowsAsync<DbUpdateException>(async () => await db.SaveChangesAsync());
        }
    }
}
