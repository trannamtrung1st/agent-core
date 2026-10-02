using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P95WorkAttention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ResultAttentionRequired",
                table: "WorkItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WorkAttentionAlerts",
                columns: table => new
                {
                    AlertKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    WorkItemId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkAttentionAlerts", x => x.AlertKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkAttentionAlerts_WorkItemId",
                table: "WorkAttentionAlerts",
                column: "WorkItemId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkAttentionAlerts");

            migrationBuilder.DropColumn(
                name: "ResultAttentionRequired",
                table: "WorkItems");
        }
    }
}
