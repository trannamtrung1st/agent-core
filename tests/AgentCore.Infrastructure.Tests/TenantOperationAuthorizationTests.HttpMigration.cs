using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture=AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantOperationAuthorizationTests
{
    [Fact]
    public async Task Tenant_http_forward_upgrade_adds_only_human_Owner_permission_and_preserves_all_existing_guards()
    {
        await using var f=await Fixture.Create(true,builtInAuthority:true);var s=await Setup(f,"general-assistant",35);
        await using var historical = await HistoricalTenantDatabase.CreateAsync(f.Contexts!,
            "20261023180000_TenantPublicDownloads", HistoricalTenantDatabase.AdmissionTables[..10]);
        var db=historical.Db;var migrator=db.GetService<IMigrator>();
        // The source was created by today's IAM owner. Reconstruct the one absent
        // predecessor permission in the isolated historical fixture only.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM AccessRolePermissions WHERE PermissionKey='http.request'");
        var oldRevision=await db.Database.SqlQuery<long>($"SELECT Revision AS Value FROM Tenants WHERE TenantId={s.Tenant}").SingleAsync();
        async Task<string> Guards()
        {
            await db.Database.OpenConnectionAsync();using var cmd=db.Database.GetDbConnection().CreateCommand();cmd.CommandText="SELECT group_concat(sql, char(10)) FROM (SELECT sql FROM sqlite_master WHERE type IN ('trigger','index') AND sql IS NOT NULL AND name NOT LIKE 'TR_TenantOperationReceipts_Http_%' ORDER BY type,name)";
            return (string)(await cmd.ExecuteScalarAsync())!;
        }
        var guards=await Guards();
        using(var cmd=db.Database.GetDbConnection().CreateCommand()) { cmd.CommandText="SELECT count(*) FROM AccessRolePermissions WHERE PermissionKey='http.request'";Assert.Equal(0L,await cmd.ExecuteScalarAsync()); }
        await migrator.MigrateAsync("20261023190000_TenantPublicHttp");
        Assert.Equal(oldRevision+1,await db.Database.SqlQuery<long>($"SELECT Revision AS Value FROM Tenants WHERE TenantId={s.Tenant}").SingleAsync());
        Assert.Equal(1,await db.Database.SqlQuery<int>($"SELECT count(*) AS Value FROM AccessRoles r JOIN AccessRolePermissions p ON p.RoleId=r.RoleId WHERE r.TenantId={s.Tenant} AND r.Key='owner' AND p.PermissionKey='http.request'").SingleAsync());
        Assert.Equal(0,await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM AccessRoles r JOIN AccessRolePermissions p ON p.RoleId=r.RoleId WHERE (r.TenantId IS NULL OR r.Key<>'owner') AND p.PermissionKey='http.request'").SingleAsync());
        Assert.Equal(guards,await Guards());Assert.False(db.Database.HasPendingModelChanges());
        using(var cmd=db.Database.GetDbConnection().CreateCommand()) { cmd.CommandText="SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_TenantOperationReceipts_Http_%'";Assert.Equal(3L,await cmd.ExecuteScalarAsync()); }
    }
}
