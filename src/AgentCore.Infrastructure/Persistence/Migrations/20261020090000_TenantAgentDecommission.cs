using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentCoreDbContext))]
[Migration("20261020090000_TenantAgentDecommission")]
public sealed class TenantAgentDecommission : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TRIGGER TR_AgentDecommission_IdentityUpdate BEFORE UPDATE ON SystemIdentities
        WHEN OLD.Kind=0 AND OLD.TenantId IS NOT NULL AND OLD.Status=2
            AND (NEW.Status IS NOT 2 OR NEW.AgentInstanceId IS NOT OLD.AgentInstanceId OR NEW.PrincipalId IS NOT OLD.PrincipalId OR NEW.TenantId IS NOT OLD.TenantId OR NEW.Kind IS NOT OLD.Kind)
        BEGIN SELECT RAISE(ABORT, 'Decommissioned Agent ownership is retained'); END;
        CREATE TRIGGER TR_AgentDecommission_IdentityDelete BEFORE DELETE ON SystemIdentities
        WHEN OLD.Kind=0 AND OLD.TenantId IS NOT NULL AND OLD.Status=2
        BEGIN SELECT RAISE(ABORT, 'Decommissioned Agent ownership is retained'); END;
        CREATE TRIGGER TR_AgentDecommission_Principal BEFORE UPDATE ON Principals
        WHEN NEW.Status<>2 AND EXISTS(SELECT 1 FROM SystemIdentities i WHERE i.PrincipalId=OLD.PrincipalId AND i.Kind=0 AND i.Status=2)
        BEGIN SELECT RAISE(ABORT, 'Decommissioned Agent cannot be restored'); END;
        CREATE TRIGGER TR_AgentDecommission_Instance BEFORE UPDATE ON AgentInstances
        WHEN NEW.Lifecycle<>'Archived' AND EXISTS(SELECT 1 FROM SystemIdentities i WHERE i.AgentInstanceId=OLD.InstanceId AND i.Kind=0 AND i.Status=2)
        BEGIN SELECT RAISE(ABORT, 'Decommissioned Agent cannot be restored'); END;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TEMP TABLE AgentDecommissionRollbackGuard (OwnedRows INTEGER CHECK(OwnedRows=0));
        INSERT INTO AgentDecommissionRollbackGuard SELECT count(*) FROM SystemIdentities WHERE Kind=0 AND TenantId IS NOT NULL AND Status=2;
        DROP TABLE AgentDecommissionRollbackGuard;
        DROP TRIGGER TR_AgentDecommission_IdentityUpdate;
        DROP TRIGGER TR_AgentDecommission_IdentityDelete;
        DROP TRIGGER TR_AgentDecommission_Principal;
        DROP TRIGGER TR_AgentDecommission_Instance;
        """);
}
