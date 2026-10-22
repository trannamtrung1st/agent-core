using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations;

// Keep all 117 existing integrity guards across SQLite's approval-table rebuild.
public partial class TenantManagementChildReads : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, "Up");
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE ManagementChildReadDowngradeGuard (Rows INTEGER CHECK(Rows=0));
            INSERT INTO ManagementChildReadDowngradeGuard SELECT
                (SELECT count(*) FROM TenantApprovalRequests WHERE json_extract(PayloadJson,'$.Operation.ManagementRead.ToolName') IN ('skills.list','skills.inspect','resources.list','resources.inspect'))
                + (SELECT count(*) FROM AgentRuns WHERE CASE WHEN json_valid(json_extract(PayloadJson,'$.checkpoint.payloadJson')) THEN EXISTS (SELECT 1 FROM json_each(json_extract(PayloadJson,'$.checkpoint.payloadJson'),'$.ExecutionState.TenantManagementReadSources') source WHERE json_extract(source.value,'$.ToolName') IN ('skills.list','skills.inspect','resources.list','resources.inspect')) ELSE 0 END);
            DROP TABLE ManagementChildReadDowngradeGuard;
            """);
        Rebuild(migrationBuilder, "Down");
    }
    internal static string ValuesConstraint
    {
        get
        {
            using var stream = typeof(TenantManagementChildReads).Assembly.GetManifestResourceStream(
                "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261022100000_TenantManagementChildReads.Check.sql")
                ?? throw new InvalidOperationException("Frozen management child-read constraint SQL is missing.");
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
    private static void Rebuild(MigrationBuilder migrationBuilder, string direction)
    {
        using var stream = typeof(TenantManagementChildReads).Assembly.GetManifestResourceStream(
            "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261022100000_TenantManagementChildReads." + direction + ".sql")
            ?? throw new InvalidOperationException("Frozen management child-read migration SQL is missing.");
        using var reader = new System.IO.StreamReader(stream);
        migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
        migrationBuilder.Sql(reader.ReadToEnd());
        migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
    }
}
