using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantUserProfileTests
{
    [Fact]
    public async Task Tenant_user_Profile_SQLite_schema_guards_real_membership_immutable_owner_revision_shape_and_provenance()
    {
        await using var f = await Fixture.Create(true); var (tenant, human) = await Start(f); var service = Service(f);
        var saved = await service.UpdateAsync(tenant, human, Change(await service.ReadAsync(tenant, human), "Protected value"));
        TenantUserProfileRecord row; await using (var db = f.Contexts!.CreateDbContext()) row = await db.Set<TenantUserProfileRecord>().AsNoTracking().SingleAsync();
        var bad = new List<string> { "[]", "{\"preferredName\":\"legacy untyped value\"}", "{\"owner\":{}}", "{\"preferredName\":null}" };
        foreach (var source in new[] { 1, 2, 99 }) { var node = JsonNode.Parse(row.PreferencesJson)!; node["preferredName"]!["source"] = source; bad.Add(node.ToJsonString()); }
        var duplicate = row.PreferencesJson[1..^1]; bad.Add("{" + duplicate + "," + duplicate + "}");
        var extra = JsonNode.Parse(row.PreferencesJson)!; extra["preferredName"]!["principalId"] = human.PrincipalId.ToString("D"); bad.Add(extra.ToJsonString());
        var future = JsonNode.Parse(row.PreferencesJson)!; future["preferredName"]!["updatedAt"] = saved.Profile.UpdatedAt.AddDays(1); bad.Add(future.ToJsonString());
        foreach (var json in bad)
        {
            await using var db = f.Contexts!.CreateDbContext(); var current = await db.Set<TenantUserProfileRecord>().SingleAsync();
            db.Entry(current).CurrentValues.SetValues(current with { Revision = current.Revision + 1, PreferencesJson = json });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        var beta = await f.Access.CreateTenantAsync((await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == human.AccountId).PrincipalId, human.AccountId, "Beta", "beta");
        var betaPrincipal = beta.Users.Single().PrincipalId;
        await using (var db = f.Contexts!.CreateDbContext())
        {
            db.Add(row with { PrincipalId = betaPrincipal, PreferencesJson = "{}" });
            var mismatch = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); Assert.Contains("FOREIGN KEY", mismatch.InnerException!.Message);
        }
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var ownerChange = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantUserProfiles SET TenantId={beta.Tenant.TenantId}, PrincipalId={betaPrincipal}, Revision=Revision+1 WHERE TenantId={tenant} AND PrincipalId={human.PrincipalId}"));
            Assert.Contains("ownership", ownerChange.Message);
            var replacement = f.Ids.NewId();
            var membership = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantUsers SET UserId={replacement} WHERE TenantId={tenant} AND PrincipalId={human.PrincipalId}"));
            Assert.Contains("membership owner", membership.Message);
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantUserProfiles SET Revision=Revision WHERE TenantId={tenant} AND PrincipalId={human.PrincipalId}"));
            var retained = await db.Set<TenantUserProfileRecord>().AsNoTracking().SingleAsync(); Assert.Equal(row, retained); Assert.False(db.Database.HasPendingModelChanges());
            var migrations = db.Database.GetMigrations().ToArray(); Assert.EndsWith("_TenantUserProfiles", migrations[^1]);
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(migrations[^2]));
        }
        Assert.Equal(saved.Profile.Revision, (await service.ReadAsync(tenant, human)).Profile.Revision);
        Assert.Single(await f.Iam.ReadAuditAsync(tenant, 100), a => a.Action == "profile.update");
    }

    [Fact]
    public async Task Tenant_user_Profile_explicit_SQLite_upgrade_preserves_identity_and_never_adopts_an_old_global_Profile()
    {
        await using var f = await Fixture.Create(true); var (tenant, human) = await Start(f);
        const string privateLegacy = "{\"preferredName\":\"Retained old global bytes\"}";
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var migrations = db.Database.GetMigrations().ToArray(); await db.GetService<IMigrator>().MigrateAsync(migrations[^2]);
            db.Add(new ProfileRecord { ProfileId = LocalUserProfile.Id.ToString("D"), PreferencesJson = privateLegacy, Revision = 1, UpdatedAtUtc = f.Clock.GetUtcNow().ToUnixTimeMilliseconds() }); await db.SaveChangesAsync();
            await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
            Assert.Empty(await db.Set<TenantUserProfileRecord>().ToArrayAsync()); Assert.Equal(privateLegacy, (await db.Set<ProfileRecord>().SingleAsync()).PreferencesJson);
        }
        var own = await Service(f).ReadAsync(tenant, human); Assert.Empty(own.Profile.Preferences); Assert.Equal(0, own.Profile.Revision);
        Assert.NotNull(await f.Identity.ValidateSessionAsync(human.AuthenticationSessionId));
    }
}
