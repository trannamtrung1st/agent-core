using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData("owner")] [InlineData("value")] [InlineData("revision")] [InlineData("reactivate")]
    [InlineData("delete")] [InlineData("parent")]
    public async Task Tenant_learned_memory_SQLite_freezes_owner_values_revision_parent_and_provenance(string boundary)
    {
        await using var f = await Fixture.Create(true); var owner = await MemoryOwner(f); var turn = await MemoryClaim(f, owner); await MemoryCommit(f, turn, await MemoryPlan(f, turn, Learned()));
        await using var db = f.Contexts!.CreateDbContext(); var item = await db.Set<TenantLearnedMemoryRecord>().OrderBy(i => i.Scope).FirstAsync();
        FormattableString sql = boundary switch {
            "owner" => $"UPDATE TenantLearnedMemories SET UserPrincipalId={owner.Agent.PrincipalId} WHERE MemoryId={item.MemoryId}",
            "value" => $"UPDATE TenantLearnedMemories SET Content={"FORGED_VALUE"}, Revision=Revision+1 WHERE MemoryId={item.MemoryId}",
            "revision" => $"UPDATE TenantLearnedMemories SET Status=1, Revision=Revision+2 WHERE MemoryId={item.MemoryId}",
            "reactivate" => $"UPDATE TenantLearnedMemories SET Status=0, Revision=Revision+1 WHERE MemoryId={item.MemoryId}",
            "parent" => $"UPDATE TenantUsers SET UserId={Guid.NewGuid()} WHERE UserId={item.UserId}",
            _ => $"DELETE FROM TenantLearnedMemories WHERE MemoryId={item.MemoryId}" };
        Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql))).SqliteErrorCode);
        db.ChangeTracker.Clear(); Assert.Equal(item, await db.Set<TenantLearnedMemoryRecord>().SingleAsync(i => i.MemoryId == item.MemoryId));
        await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync("20261021144350_TenantUserProfiles"));
        Assert.Equal(3, await db.Set<TenantLearnedMemoryRecord>().CountAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
    [Fact]
    public async Task Tenant_learned_memory_explicit_empty_upgrade_preserves_installed_identity_and_global_rows_without_adopting_them()
    {
        await using var f = await Fixture.Create(true); var owner = await MemoryOwner(f); var turn = await MemoryClaim(f, owner);
        await using var db = f.Contexts!.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261021144350_TenantUserProfiles");
        var id = Guid.NewGuid().ToString("D"); var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        db.Profiles.Add(new() { ProfileId = Guid.NewGuid().ToString("D"), PreferencesJson = "{}", Revision = 1, UpdatedAtUtc = now });
        db.StructuredMemories.Add(new() { MemoryId = id, SessionId = turn.Frame.SessionId.ToString("D"), Kind = 0, Status = 0,
            Subject = "Old global source", SubjectKey = "old global source", Content = "DO_NOT_ADOPT_GLOBAL_MEMORY", Source = "test",
            SourceEntryIdsJson = "[]", ProvenanceRecordedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now, Scope = 0 });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await db.GetService<IMigrator>().MigrateAsync();
        Assert.Single(await db.StructuredMemories.ToArrayAsync()); Assert.Single(await db.Profiles.ToArrayAsync());
        Assert.True(await db.Set<AuthenticationSession>().AnyAsync(s => s.SessionId == owner.Human.AuthenticationSessionId));
        Assert.Empty(await db.Set<TenantLearnedMemoryRecord>().ToArrayAsync()); Assert.Empty((await ((ITenantLearnedMemoryStore)f.Iam).ReadTenantRunMemoryAsync(turn.Frame)).Items);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
