using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentCoreDbContext))]
[Migration("20261028190100_FormalWorkflowRetention")]
public sealed class FormalWorkflowRetention : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => Apply(migrationBuilder, "Up");
    protected override void Down(MigrationBuilder migrationBuilder) => Apply(migrationBuilder, "Down");
    private static void Apply(MigrationBuilder migrationBuilder, string direction)
    {
        using var stream = typeof(FormalWorkflowRetention).Assembly.GetManifestResourceStream($"AgentCore.Infrastructure.Persistence.Migrations.Sql.20261028190100_FormalWorkflowRetention.{direction}.sql")!;
        using var reader = new StreamReader(stream); migrationBuilder.Sql(reader.ReadToEnd());
    }
}
