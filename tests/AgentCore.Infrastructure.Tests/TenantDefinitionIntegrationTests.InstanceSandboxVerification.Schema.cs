using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Fact]
    public async Task Tenant_instance_sandbox_verification_schema_rejects_foreign_instance_null_pin_and_profile_evidence_promotion()
    {
        await using var f = await Fixture.Create(true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); var p = SetupProfiles(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var profile = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("Relational scope"), await Revision(), 0)).Profile;
        var s = await SetupAgent(f, owner.TenantId, human.PrincipalId, new(profile.Id, [profile.Id]), p.Images, p.Ready); var id = s.Agent.Instance.InstanceId;
        var result = await InstanceVerification(f, p).VerifyAsync(owner.TenantId, id, profile.Id, human, await Revision(), 1, 1); var attempt = result.Attempt!.AttemptId;
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(x => x.AccountId == owner.AccountId).PrincipalId;
        var beta = await f.Access.CreateTenantAsync(system, owner.AccountId, "Beta", "beta");
        var other = await ConfigurationServices(f).Agents.CreateAsync(beta.Users.Single().PrincipalId, beta.Tenant.TenantId, "examiner", 1, beta.Tenant.Revision);
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var foreign = other.Instance.InstanceId.ToString("D");
            var fk = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantSandboxVerificationAttempts SET InstanceId={foreign}, Json=json_set(Json, '$.instanceId', {foreign}) WHERE AttemptId={attempt}"));
            Assert.Contains("FOREIGN KEY", fk.Message);
            var check = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantSandboxVerificationAttempts SET InstanceRevision=NULL, Json=json_set(Json, '$.instanceRevision', NULL) WHERE AttemptId={attempt}"));
            Assert.Contains("CK_SandboxAttempt_InstanceJson", check.Message);
            db.Add(new SandboxVerificationRow { TenantId = owner.TenantId, AttemptId = attempt, ProfileId = profile.Id, ProfileRevision = 1,
                Json = JsonSerializer.Serialize(result.Observation, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            var promotion = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("CK_SandboxVerification_TenantJson", promotion.InnerException!.Message);
        }
        Assert.Equal(id, (await ((ITenantSandboxVerificationStore)f.Iam).FindSandboxVerificationAsync(owner.TenantId, profile.Id, attempt, instanceId: id))!.InstanceId);
        Assert.Null((await ((ITenantSandboxVerificationStore)f.Iam).ReadSandboxVerificationAsync(owner.TenantId, profile.Id, 1)).Observation);
    }

    [Fact]
    public async Task Tenant_instance_sandbox_verification_migration_preserves_existing_reusable_full_receipts_and_observations()
    {
        await using var f = await Fixture.Create(true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); var p = SetupProfiles(f);
        var before = (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var profile = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("Retained evidence"), before, 0)).Profile;
        var now = DateTimeOffset.UtcNow; var revision = (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var observation = new SandboxProfileVerification(profile.Id, 1, now, true, "Existing full result", ContractVersion: SandboxProfileVerification.CurrentContractVersion, TenantId: owner.TenantId, HostScope: p.Host.Scope);
        var attempt = new SandboxVerificationAttempt(Guid.NewGuid(), owner.TenantId, profile.Id, 1, false, 2, SandboxVerificationStatus.Succeeded, now.AddSeconds(-1), now,
            human.AccountId, human.AuthenticationSessionId, human.PrincipalId, revision, p.Host.Scope, Result: observation);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var db = f.Contexts!.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261017203239_TenantSandboxVerificationOwnership");
            var receipt = JsonSerializer.Serialize(attempt, json);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO TenantSandboxVerificationAttempts (AttemptId,TenantId,ProfileId,ProfileRevision,Revision,SecurityRevision,Active,LeaseExpiresAt,StartedAt,Json) VALUES ({attempt.AttemptId},{owner.TenantId},{profile.Id},{1L},{2L},{revision},{false},{now.ToUnixTimeMilliseconds()},{attempt.StartedAt.ToUnixTimeMilliseconds()},{receipt})");
            var value = JsonSerializer.Serialize(observation, json);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO SandboxProfileVerifications (TenantId,AttemptId,ProfileId,ProfileRevision,Json) VALUES ({owner.TenantId},{attempt.AttemptId},{profile.Id},{1L},{value})");
            await db.GetService<IMigrator>().MigrateAsync();
        }
        var reopened = new SqliteIamStore(f.Contexts!, f.Content);
        var retained = (await reopened.FindSandboxVerificationAsync(owner.TenantId, profile.Id, attempt.AttemptId))!;
        Assert.Equal(SandboxVerificationStatus.Succeeded, retained.Status); Assert.True(retained.Result!.Passed); Assert.Null(retained.InstanceId);
        Assert.True((await reopened.ReadSandboxVerificationAsync(owner.TenantId, profile.Id, 1)).Observation!.Passed);
        Assert.True((await p.Images.InspectAsync(profile)).Eligible);
        Assert.Equal(revision, (await reopened.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
    }
}
