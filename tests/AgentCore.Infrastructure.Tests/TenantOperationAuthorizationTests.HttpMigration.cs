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
        await using var db=f.Contexts!.CreateDbContext();var migrator=db.GetService<IMigrator>();var before=(await f.Iam.ReadTenantAsync(s.Tenant))!;
        async Task<string> Guards()
        {
            await db.Database.OpenConnectionAsync();using var cmd=db.Database.GetDbConnection().CreateCommand();cmd.CommandText="SELECT group_concat(sql, char(10)) FROM (SELECT sql FROM sqlite_master WHERE type IN ('trigger','index') AND sql IS NOT NULL AND name NOT LIKE 'TR_TenantOperationReceipts_Http_%' ORDER BY type,name)";
            return (string)(await cmd.ExecuteScalarAsync())!;
        }
        var guards=await Guards();await migrator.MigrateAsync("20261023180000_TenantPublicDownloads");
        var old=await db.Set<Tenant>().AsNoTracking().SingleAsync(t=>t.TenantId==s.Tenant);
        using(var cmd=db.Database.GetDbConnection().CreateCommand()) { cmd.CommandText="SELECT count(*) FROM AccessRolePermissions WHERE PermissionKey='http.request'";Assert.Equal(0L,await cmd.ExecuteScalarAsync()); }
        Assert.Equal(before.Tenant.Revision+1,old.Revision);Assert.Equal(guards,await Guards());
        await migrator.MigrateAsync();var after=(await f.Iam.ReadTenantAsync(s.Tenant))!;
        Assert.Equal(old.Revision+1,after.Tenant.Revision);Assert.Contains("http.request",after.Roles.Single(r=>r.Key==BuiltinAccessRoles.Owner).Permissions);
        Assert.DoesNotContain(after.Roles.Where(r=>r.Key!=BuiltinAccessRoles.Owner),r=>r.Permissions.Contains("http.request"));
        Assert.Equal(guards,await Guards());Assert.False(db.Database.HasPendingModelChanges());
        using(var cmd=db.Database.GetDbConnection().CreateCommand()) { cmd.CommandText="SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_TenantOperationReceipts_Http_%'";Assert.Equal(3L,await cmd.ExecuteScalarAsync()); }
    }
}
