using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class TenantManagementSkillAuthoring : Migration
{
    internal static string ValuesConstraint => Read("Check");
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE TenantOperationReceipts ADD COLUMN ManagementSkillJson TEXT NULL CONSTRAINT CK_TenantOperationReceipt_ManagementSkill CHECK (" + AgentCore.Infrastructure.Security.ManagementSkillReceiptPersistence.Shape + ");");
        Rebuild(migrationBuilder, "Up");
        migrationBuilder.Sql(Read("Guards"));
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE ManagementSkillDowngradeGuard (Rows INTEGER CHECK(Rows=0));
            INSERT INTO ManagementSkillDowngradeGuard SELECT
                (SELECT count(*) FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.ManagementMutation.Skill')='object')
                + (SELECT count(*) FROM TenantOperationReceipts WHERE ManagementSkillJson IS NOT NULL);
            DROP TABLE ManagementSkillDowngradeGuard;
            DROP TRIGGER IF EXISTS TR_TenantOperationReceipts_ManagementSkill_INSERT;
            DROP TRIGGER IF EXISTS TR_TenantOperationReceipts_ManagementSkill_UPDATE;
            """);
        Rebuild(migrationBuilder, "Down");
        migrationBuilder.Sql("ALTER TABLE TenantOperationReceipts DROP COLUMN ManagementSkillJson;");
    }
    private static string Read(string kind)
    {
        using var stream = typeof(TenantManagementSkillAuthoring).Assembly.GetManifestResourceStream(
            "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261022110000_TenantManagementSkillAuthoring." + kind + ".sql")
            ?? throw new InvalidOperationException("Frozen Skill authoring migration SQL is missing.");
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static void Rebuild(MigrationBuilder migrationBuilder, string direction)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
        migrationBuilder.Sql(Read(direction));
        migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
    }
}
