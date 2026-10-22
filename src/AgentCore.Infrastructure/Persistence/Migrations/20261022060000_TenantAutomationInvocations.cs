using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations;

// Keep all 110 existing integrity guards across SQLite's approval-table rebuild.
public partial class TenantAutomationInvocations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, "Up");
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE AutomationInvocationDowngradeGuard (Rows INTEGER CHECK(Rows=0));
            INSERT INTO AutomationInvocationDowngradeGuard SELECT
                (SELECT count(*) FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.AutomationMutation.Invocation')='object')
                + (SELECT count(*) FROM TenantOperationReceipts WHERE json_extract(AutomationMutationJson,'$.ToolName')='automation.run');
            DROP TABLE AutomationInvocationDowngradeGuard;
            """);
        Rebuild(migrationBuilder, "Down");
    }
    internal static string ValuesConstraint
    {
        get
        {
            using var stream = typeof(TenantAutomationInvocations).Assembly.GetManifestResourceStream(
                "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261022060000_TenantAutomationInvocations.Check.sql")
                ?? throw new InvalidOperationException("Frozen Automation mutation constraint SQL is missing.");
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
    private static void Rebuild(MigrationBuilder migrationBuilder, string direction)
    {
        using var stream = typeof(TenantAutomationInvocations).Assembly.GetManifestResourceStream(
            "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261022060000_TenantAutomationInvocations." + direction + ".sql")
            ?? throw new InvalidOperationException("Frozen Automation mutation migration SQL is missing.");
        using var reader = new System.IO.StreamReader(stream);
        migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
        migrationBuilder.Sql(reader.ReadToEnd());
        migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
    }
}
