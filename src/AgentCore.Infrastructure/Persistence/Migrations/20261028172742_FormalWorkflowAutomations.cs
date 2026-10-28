using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class FormalWorkflowAutomations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql(Read("Up"));
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE WorkflowAutomationRollbackGuard(Rows INTEGER CONSTRAINT restore_coordinated_workflow_backup_required CHECK(Rows=0));
            INSERT INTO WorkflowAutomationRollbackGuard SELECT count(*) FROM Automations WHERE ExecutionTargetKind=2;
            INSERT INTO WorkflowAutomationRollbackGuard SELECT count(*) FROM TriggerOccurrences WHERE ExecutionTargetKind=2;
            DROP TABLE WorkflowAutomationRollbackGuard;
            """);
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql(Read("Down"));
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }
    private static string Read(string direction)
    {
        using var stream = typeof(FormalWorkflowAutomations).Assembly.GetManifestResourceStream("AgentCore.Infrastructure.Persistence.Migrations.Sql.20261028172742_FormalWorkflowAutomations." + direction + ".sql")
            ?? throw new System.InvalidOperationException("Frozen Workflow Automation migration SQL is missing.");
        using var reader = new System.IO.StreamReader(stream); return reader.ReadToEnd();
    }
}
