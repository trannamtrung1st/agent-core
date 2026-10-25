using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Spaces;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Environment_typed_variables_and_Secret_encryption_CAS_reference_lifecycle_and_reopen(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var keyRoot = Directory.CreateTempSubdirectory("space-secret-keys-");
        try
        {
            var owner = await f.Identity.BootstrapAsync("env-owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
            var actor = owner.OwnerPrincipalId; var tenant = owner.TenantId;
            async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
            var builtIns = new SessionNoBuiltinFallback(); var resources = new TenantRunConfigurationResolver((ITenantAgentStore)f.Iam, f.Definitions, builtIns,
                new(f.Resources, builtIns, new NoBuiltinKnowledge(), f.Content), (ITenantCredentialBindingStore)f.Iam);
            var spaceStore = (ITenantSpaceStore)f.Iam; var secretStore = (ITenantSecretStore)f.Iam; var envStore = (ITenantEnvironmentStore)f.Iam;
            var spaces = new TenantSpaceService(f.Iam, spaceStore, (ITenantAgentStore)f.Iam, f.Authorizer, resources, (ITenantCredentialBindingStore)f.Iam, (ITenantCredentialStore)f.Iam, f.Ids, TimeProvider.System, secretStore);
            ISecretProtector protector = new LocalCredentialProtector(keyRoot.FullName);
            var secrets = new TenantSecretService(f.Iam, secretStore, f.Authorizer, protector, f.Ids, TimeProvider.System, f.Identity);
            var env = new TenantEnvironmentService(f.Iam, spaceStore, envStore, secretStore, (ITenantCredentialStore)f.Iam, f.Authorizer, f.Identity, f.Ids, TimeProvider.System);
            var room = await spaces.CreateAsync(actor, tenant, f.Ids.NewId(), "Testing", "", "", await Revision());
            const string sentinel = "ENV_SECRET_SENTINEL_never_model_visible";
            var secret = await secrets.CreateAsync(actor, tenant, "Test token", "Protected test value", sentinel, await Revision());
            var stored = (await secretStore.FindSecretAsync(tenant, secret.SecretId))!;
            Assert.DoesNotContain(sentinel, stored.ProtectedPayload); Assert.Equal(sentinel, protector.Unprotect(tenant, secret.SecretId, 1, stored.ProtectedPayload));
            Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(secret)); Assert.DoesNotContain(stored.ProtectedPayload, JsonSerializer.Serialize(stored));
            await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => Task.FromResult(protector.Unprotect(Guid.NewGuid(), secret.SecretId, 1, stored.ProtectedPayload)));
            await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => Task.FromResult(((ICredentialProtector)new LocalCredentialProtector(keyRoot.FullName)).Unprotect(secret.SecretId, 1, stored.ProtectedPayload)));
            var profile = await env.SaveAsync(actor, tenant, room.SpaceId, f.Ids.NewId(), "Development", "Saved while disabled", await Revision(), room.Revision, 0);
            Assert.False((await spaces.ReadAsync(actor, tenant, room.SpaceId)).EnvironmentsEnabled);
            await Assert.ThrowsAsync<SecurityConflictException>(async () => await env.SaveAsync(actor, tenant, room.SpaceId, f.Ids.NewId(), "development", "Duplicate", await Revision(), room.Revision, 0));
            var plain = await env.SaveVariableAsync(actor, tenant, room.SpaceId, profile.EnvironmentId, "build_mode", EnvironmentVariableKind.Plain,
                "Visible only on authorized lookup", "PLAIN_ON_DEMAND", null, null, await Revision(), room.Revision, 1, 0);
            Assert.Equal("BUILD_MODE", plain.Name); Assert.DoesNotContain("PLAIN_ON_DEMAND", JsonSerializer.Serialize(plain));
            await env.SaveVariableAsync(actor, tenant, room.SpaceId, profile.EnvironmentId, "token", EnvironmentVariableKind.SecretReference,
                "Protected reference", null, secret.SecretId, null, await Revision(), room.Revision, 2, 0);
            var rows = await env.VariablesAsync(actor, tenant, room.SpaceId, profile.EnvironmentId); Assert.Equal(2, rows.Count);
            Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(rows)); Assert.DoesNotContain("PLAIN_ON_DEMAND", JsonSerializer.Serialize(rows));
            Assert.Equal("PLAIN_ON_DEMAND", (await envStore.ReadVariablesAsync(tenant, room.SpaceId, profile.EnvironmentId)).Single(v => v.Name == "BUILD_MODE").PlainValue);
            await Assert.ThrowsAsync<SecurityConflictException>(async () => await secrets.DeleteAsync(actor, tenant, secret.SecretId, await Revision(), secret.Revision));
            await Assert.ThrowsAsync<SecurityConflictException>(async () => await env.SaveVariableAsync(actor, tenant, room.SpaceId, profile.EnvironmentId, "other", EnvironmentVariableKind.Plain, "", "stale", null, null, await Revision(), room.Revision, 2, 0));
            await Assert.ThrowsAsync<ArgumentException>(async () => await env.SaveVariableAsync(actor, tenant, room.SpaceId, profile.EnvironmentId, "bad", EnvironmentVariableKind.SecretReference, "", "unsafe", secret.SecretId, null, await Revision(), room.Revision, 3, 0));
            await Assert.ThrowsAsync<AccessDeniedException>(async () => await env.SaveVariableAsync(actor, tenant, room.SpaceId, profile.EnvironmentId, "foreign", EnvironmentVariableKind.SecretReference, "", null, Guid.NewGuid(), null, await Revision(), room.Revision, 3, 0));
            var enabled = await spaces.UpdateAsync(actor, tenant, room.SpaceId, room.Name, "", "", true, await Revision(), room.Revision);
            Assert.Single(await env.ListAsync(actor, tenant, room.SpaceId));
            room = await spaces.UpdateAsync(actor, tenant, room.SpaceId, room.Name, "", "", false, await Revision(), enabled.Revision);
            Assert.Single(await env.ListAsync(actor, tenant, room.SpaceId));
            Assert.Equal(2, (await env.VariablesAsync(actor, tenant, room.SpaceId, profile.EnvironmentId)).Count);
            if (sqlite)
            {
                var reopened = new SqliteIamStore(f.Contexts!, f.Content);
                var recovered = (await reopened.FindSecretAsync(tenant, secret.SecretId))!;
                Assert.Equal(sentinel, ((ISecretProtector)new LocalCredentialProtector(keyRoot.FullName)).Unprotect(tenant, secret.SecretId, 1, recovered.ProtectedPayload));
                Assert.Equal(2, (await reopened.ReadVariablesAsync(tenant, room.SpaceId, profile.EnvironmentId)).Count);
                await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
            }
            await env.DeleteAsync(actor, tenant, room.SpaceId, profile.EnvironmentId, await Revision(), room.Revision, 3);
            Assert.Empty(await env.ListAsync(actor, tenant, room.SpaceId));
            await Assert.ThrowsAsync<SecurityConflictException>(async () => await env.SaveAsync(actor, tenant, room.SpaceId, profile.EnvironmentId, "Reused ID", "", await Revision(), room.Revision, 0));
            await secrets.DeleteAsync(actor, tenant, secret.SecretId, await Revision(), secret.Revision);
            Assert.Null(await secretStore.FindSecretAsync(tenant, secret.SecretId));
            Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(await f.Iam.ReadAuditAsync(tenant, 100)));
        }
        finally { keyRoot.Delete(true); }
    }
}
