using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class TenantPrivateSkillActivation : Migration
{
    internal static string ValuesConstraint => Read("Check");
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, "Up");
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE PrivateSkillDowngradeGuard (Rows INTEGER CHECK(Rows=0));
            INSERT INTO PrivateSkillDowngradeGuard SELECT
                (SELECT count(*) FROM TenantApprovalRequests WHERE json_extract(PayloadJson,'$.Operation.ManagementRead.ToolName')='skills.load')
                + (SELECT count(*) FROM AgentRuns WHERE CASE WHEN json_valid(json_extract(PayloadJson,'$.checkpoint.payloadJson')) THEN EXISTS (SELECT 1 FROM json_each(json_extract(PayloadJson,'$.checkpoint.payloadJson'),'$.ExecutionState.TenantManagementReadSources') source WHERE json_extract(source.value,'$.ToolName')='skills.load') ELSE 0 END);
            DROP TABLE PrivateSkillDowngradeGuard;
            """);
        Rebuild(migrationBuilder, "Down");
    }
    private static string Read(string kind)
    {
        using var stream = typeof(TenantPrivateSkillActivation).Assembly.GetManifestResourceStream(
            "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261022120000_TenantPrivateSkillActivation." + kind + ".sql")
            ?? throw new InvalidOperationException("Frozen private Skill activation migration SQL is missing.");
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
