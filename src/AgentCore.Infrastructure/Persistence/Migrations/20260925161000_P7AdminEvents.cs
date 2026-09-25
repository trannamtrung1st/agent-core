using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P7AdminEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    OccurredAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: true),
                    SummaryJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminEvents", x => x.EventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminEvents_OperationId",
                table: "AdminEvents",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminEvents_TargetType_TargetId_OccurredAtUtc",
                table: "AdminEvents",
                columns: new[] { "TargetType", "TargetId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminEvents");
        }
    }
}
