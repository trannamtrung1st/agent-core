using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class SpaceWorkspaceOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, true);
    protected override void Down(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, false);

    private static void Rebuild(MigrationBuilder migrationBuilder, bool up)
    {
        migrationBuilder.Sql(up ? """
            CREATE TEMP TABLE SpaceWorkspaceCutoverGuard(ExistingRows INTEGER CHECK(ExistingRows=0));
            INSERT INTO SpaceWorkspaceCutoverGuard SELECT (SELECT count(*) FROM Tenants)+(SELECT count(*) FROM AgentInstances)
                +(SELECT count(*) FROM Sessions)+(SELECT count(*) FROM AgentRuns)+(SELECT count(*) FROM SandboxSessionLeases);
            DROP TABLE SpaceWorkspaceCutoverGuard;
            """ : """
            CREATE TEMP TABLE SpaceWorkspaceDowngradeGuard(ExistingRows INTEGER CHECK(ExistingRows=0));
            INSERT INTO SpaceWorkspaceDowngradeGuard SELECT (SELECT count(*) FROM Spaces)
                +(SELECT count(*) FROM AgentRuns WHERE TenantId IS NOT NULL)
                +(SELECT count(*) FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.SpaceContext')='object');
            DROP TABLE SpaceWorkspaceDowngradeGuard;
            """);
        using var resource = typeof(SpaceWorkspaceOwnership).Assembly.GetManifestResourceStream(
            "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261025040000_SpaceWorkspaceOwnership.Previous.json")
            ?? throw new InvalidOperationException("Frozen workspace predecessor guards are missing.");
        using var memory = new MemoryStream(); resource.CopyTo(memory); var bytes = memory.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != "9e94cea85a786056cc3ff67ff99b56de9df6ce9faa76d2b529d35cc140eb4dc7")
            throw new InvalidOperationException("Frozen workspace predecessor guards changed.");
        var previous = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes)!;
        var source = TenantPublicDownloads.FrozenPredecessor().Previous["table:TenantApprovalRequests"];
        var ddl = TenantPublicDownloads.ReplaceCheck(source, up ? SpaceWorkspaceApprovalPersistence.ValuesConstraint : TenantPublicHttp.ValuesConstraint);
        var offset = ddl.IndexOf("TenantApprovalRequests", StringComparison.Ordinal);
        var sql = new StringBuilder();
        if (!up) sql.AppendLine("DROP TRIGGER TR_TenantApprovals_Space_Workspace_INSERT;");
        foreach (var key in previous.Keys.Where(k => k.StartsWith("trigger:", StringComparison.Ordinal))) sql.AppendLine("DROP TRIGGER " + key[8..] + ";");
        sql.AppendLine(ddl[..offset] + "__space_workspace_TenantApprovalRequests" + ddl[(offset + "TenantApprovalRequests".Length)..] + ";");
        sql.AppendLine("INSERT INTO __space_workspace_TenantApprovalRequests SELECT * FROM TenantApprovalRequests; DROP TABLE TenantApprovalRequests; ALTER TABLE __space_workspace_TenantApprovalRequests RENAME TO TenantApprovalRequests;");
        foreach (var value in previous.Where(p => p.Key.StartsWith("index:", StringComparison.Ordinal)).Select(p => p.Value)) sql.AppendLine(value.TrimEnd(';') + ";");
        foreach (var value in previous.Where(p => p.Key.StartsWith("trigger:", StringComparison.Ordinal)).Select(p => p.Value)) sql.AppendLine(value.TrimEnd(';') + ";");
        if (up) sql.AppendLine(Guards);
        migrationBuilder.Sql("PRAGMA foreign_keys=0;", suppressTransaction: true);
        migrationBuilder.Sql(sql.ToString());
        migrationBuilder.Sql("PRAGMA foreign_keys=1;", suppressTransaction: true);
    }

    private const string Guards = """
        CREATE TRIGGER TR_TenantApprovals_Space_Workspace_INSERT BEFORE INSERT ON TenantApprovalRequests
        BEGIN
            SELECT RAISE(ABORT,'Prepared context is no longer available') WHERE NOT EXISTS(
                SELECT 1 FROM Spaces space JOIN SpaceMemberships member ON member.TenantId=space.TenantId AND member.SpaceId=space.SpaceId
                JOIN SystemIdentities identity ON identity.TenantId=member.TenantId AND identity.AgentInstanceId=member.InstanceId AND identity.PrincipalId=NEW.ExecutorPrincipalId AND identity.Status=0
                JOIN Principals principal ON principal.PrincipalId=identity.PrincipalId AND principal.TenantId=identity.TenantId AND principal.Status=0
                JOIN Spaces own ON own.TenantId=space.TenantId AND own.Kind=0 AND own.OwnerInstanceId=NEW.InstanceId AND own.DeletedAt IS NULL
                WHERE space.TenantId=NEW.TenantId AND lower(space.SpaceId)=json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.SpaceId')
                    AND member.InstanceId=NEW.InstanceId AND space.DeletedAt IS NULL
                    AND (space.Kind=1 OR space.OwnerInstanceId=NEW.InstanceId)
                    AND lower(own.SpaceId)=json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.PrivateSpaceId')
                    AND (json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.EnvironmentId') IS NULL OR (space.EnvironmentsEnabled=1
                        AND space.EnvironmentSelectionEpoch=json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.EnvironmentSelectionEpoch')
                        AND EXISTS(SELECT 1 FROM SpaceEnvironments environment WHERE environment.TenantId=space.TenantId AND environment.SpaceId=space.SpaceId
                            AND lower(environment.EnvironmentId)=json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.EnvironmentId') AND environment.DeletedAt IS NULL))));
            SELECT RAISE(ABORT,'Workspace mutation requires exact logical owner roots')
                WHERE json_type(NEW.PayloadJson,'$.Operation.WorkspaceMutation')='object' AND EXISTS(
                    SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.WorkspaceMutation.TargetsJson')) target
                    WHERE NOT COALESCE(json_type(target.value,'$.Root')='text' AND json_extract(target.value,'$.Root') IN ('/home','/private','/working')
                        AND json_type(target.value,'$.Working') IN ('true','false')
                        AND json_extract(target.value,'$.Working')=(json_extract(target.value,'$.Root')='/working')
                        AND json_extract(target.value,'$.Permission') IN ('workspaces.read','workspaces.write','workspaces.delete')
                        AND (SELECT count(*) FROM json_each(target.value))=3,0));
            SELECT RAISE(ABORT,'Sandbox command requires its captured workspace access witness')
                WHERE json_type(NEW.PayloadJson,'$.Operation.SandboxCommand')='object' AND NOT COALESCE(
                    json_type(NEW.PayloadJson,'$.Operation.SandboxCommand.WorkspaceAccess')='object'
                    AND json_extract(NEW.PayloadJson,'$.Operation.SandboxCommand.WorkspaceAccess.Context')=json_extract(NEW.PayloadJson,'$.Operation.SpaceContext')
                    AND json_extract(NEW.PayloadJson,'$.Operation.SandboxCommand.HomeAccess')=json_extract(NEW.PayloadJson,'$.Operation.SandboxCommand.WorkspaceAccess.HomeAccess')
                    AND json_type(NEW.PayloadJson,'$.Operation.SandboxCommand.WorkspaceAccess.HomeAccess')='text'
                    AND json_extract(NEW.PayloadJson,'$.Operation.SandboxCommand.WorkspaceAccess.HomeAccess') IN ('ReadWrite','ReadOnly','Disabled')
                    AND json_type(NEW.PayloadJson,'$.Operation.SandboxCommand.WorkspaceAccess.PrivateAccess')='text'
                    AND json_extract(NEW.PayloadJson,'$.Operation.SandboxCommand.WorkspaceAccess.PrivateAccess') IN ('ReadWrite','ReadOnly','Disabled')
                    AND json_type(NEW.PayloadJson,'$.Operation.SandboxCommand.WorkspaceAccess.WorkingReadOnly') IN ('true','false'),0);
        END;
        """;
}
