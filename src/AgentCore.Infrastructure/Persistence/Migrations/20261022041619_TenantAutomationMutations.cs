using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations;

// Keep all 106 existing integrity guards across SQLite's approval-table rebuild.
public partial class TenantAutomationMutations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, "Up");
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE AutomationMutationDowngradeGuard (Rows INTEGER CHECK(Rows=0));
            INSERT INTO AutomationMutationDowngradeGuard SELECT
                (SELECT count(*) FROM TenantApprovalRequests WHERE json_extract(PayloadJson,'$.Operation.AutomationMutation') IS NOT NULL)
                + (SELECT count(*) FROM TenantOperationReceipts WHERE AutomationMutationJson IS NOT NULL)
                + (SELECT count(*) FROM Automations WHERE TenantId IS NOT NULL AND AuthorizationOrigin=4);
            DROP TABLE AutomationMutationDowngradeGuard;
            """);
        Rebuild(migrationBuilder, "Down");
    }
    internal static string ValuesConstraint
    {
        get
        {
            using var stream = typeof(TenantAutomationMutations).Assembly.GetManifestResourceStream(
                "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261022041619_TenantAutomationMutations.Check.sql")
                ?? throw new InvalidOperationException("Frozen Automation mutation constraint SQL is missing.");
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
    private static void Rebuild(MigrationBuilder migrationBuilder, string direction)
    {
        using var stream = typeof(TenantAutomationMutations).Assembly.GetManifestResourceStream(
            "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261022041619_TenantAutomationMutations." + direction + ".sql")
            ?? throw new InvalidOperationException("Frozen Automation mutation migration SQL is missing.");
        using var reader = new System.IO.StreamReader(stream);
        migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
        migrationBuilder.Sql(reader.ReadToEnd());
        migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
    }
}
