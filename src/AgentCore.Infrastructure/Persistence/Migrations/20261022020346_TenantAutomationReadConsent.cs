using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations;

// Keep all 104 existing integrity guards across SQLite's approval-table rebuild.
public partial class TenantAutomationReadConsent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, "Up");
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE AutomationReadDowngradeGuard (Rows INTEGER CHECK(Rows=0));
            INSERT INTO AutomationReadDowngradeGuard SELECT count(*) FROM TenantApprovalRequests
                WHERE json_extract(PayloadJson,'$.Operation.AutomationRead') IS NOT NULL;
            DROP TABLE AutomationReadDowngradeGuard;
            """);
        Rebuild(migrationBuilder, "Down");
    }
    internal static string ValuesConstraint
    {
        get
        {
            using var stream = typeof(TenantAutomationReadConsent).Assembly.GetManifestResourceStream(
                "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261022020346_TenantAutomationReadConsent.Check.sql")
                ?? throw new InvalidOperationException("Frozen Automation read constraint SQL is missing.");
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
    private static void Rebuild(MigrationBuilder migrationBuilder, string direction)
    {
        using var stream = typeof(TenantAutomationReadConsent).Assembly.GetManifestResourceStream(
            "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261022020346_TenantAutomationReadConsent." + direction + ".sql")
            ?? throw new InvalidOperationException("Frozen Automation read migration SQL is missing.");
        using var reader = new System.IO.StreamReader(stream);
        migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
        migrationBuilder.Sql(reader.ReadToEnd());
        migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
    }
}
