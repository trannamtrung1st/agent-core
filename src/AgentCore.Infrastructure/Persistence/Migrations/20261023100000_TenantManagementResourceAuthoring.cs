using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class TenantManagementResourceAuthoring : Migration
{
    internal static string ValuesConstraint => Read("Check");
    internal static string ReceiptConstraint => Read("Receipt");
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder,
        "ALTER TABLE TenantOperationReceipts ADD COLUMN ManagementResourceJson TEXT NULL CONSTRAINT CK_TenantOperationReceipt_ManagementResource CHECK (" + ReceiptConstraint + ");\n"
        + Read("Up") + "\n" + Read("Guards"));
    protected override void Down(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder,
        """
        CREATE TEMP TABLE ManagementResourceDowngradeGuard (Rows INTEGER CHECK(Rows=0));
        INSERT INTO ManagementResourceDowngradeGuard SELECT
            (SELECT count(*) FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.ManagementMutation.Resource')='object')
            + (SELECT count(*) FROM TenantOperationReceipts WHERE ManagementResourceJson IS NOT NULL);
        DROP TABLE ManagementResourceDowngradeGuard;
        """ + "\n" + Read("Down") + "\nALTER TABLE TenantOperationReceipts DROP COLUMN ManagementResourceJson;");

    private static string Read(string kind)
    {
        using var stream = typeof(TenantManagementResourceAuthoring).Assembly.GetManifestResourceStream(
            "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261023100000_TenantManagementResourceAuthoring." + kind + ".sql")
            ?? throw new InvalidOperationException("Frozen Resource authoring migration SQL is missing.");
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static void Rebuild(MigrationBuilder migrationBuilder, string sql)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
        migrationBuilder.Sql(sql);
        migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
    }
}
