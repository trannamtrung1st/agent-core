using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations;

// Keep all 114 existing integrity guards across SQLite's approval-table rebuild.
public partial class TenantManagementMutations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, "Up");
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE ManagementMutationDowngradeGuard (Rows INTEGER CHECK(Rows=0));
            INSERT INTO ManagementMutationDowngradeGuard SELECT
                (SELECT count(*) FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.ManagementMutation')='object')
                + (SELECT count(*) FROM TenantOperationReceipts WHERE ReasonCode='management_mutation_completed');
            DROP TABLE ManagementMutationDowngradeGuard;
            """);
        Rebuild(migrationBuilder, "Down");
    }
    internal static string ValuesConstraint
    {
        get
        {
            using var stream = typeof(TenantManagementMutations).Assembly.GetManifestResourceStream(
                "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261022080000_TenantManagementMutations.Check.sql")
                ?? throw new InvalidOperationException("Frozen management mutation constraint SQL is missing.");
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
    private static void Rebuild(MigrationBuilder migrationBuilder, string direction)
    {
        using var stream = typeof(TenantManagementMutations).Assembly.GetManifestResourceStream(
            "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261022080000_TenantManagementMutations." + direction + ".sql")
            ?? throw new InvalidOperationException("Frozen management mutation migration SQL is missing.");
        using var reader = new System.IO.StreamReader(stream);
        migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
        migrationBuilder.Sql(reader.ReadToEnd());
        migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
    }
}
